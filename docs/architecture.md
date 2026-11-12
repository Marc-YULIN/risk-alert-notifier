# Architecture

## Components

```mermaid
flowchart TB
  subgraph Clients
    Simulator[Event simulator]
    Producer[Transaction producer]
    Dashboard[Native JS dashboard]
  end
  subgraph API[ASP.NET Core API / single process]
    Ingest[API-key ingestion endpoints]
    Operator[Read / configuration / operator endpoints]
    Gate[Single-process write gate]
    Engine[RuleEngine]
    Cache[RuleSettingsProvider / 30-second cache]
    Rules[IRiskRule implementations]
    Hub[AlertsHub / currency groups]
  end
  subgraph Infrastructure
    DB[(SQLite / fixed-point money / UTC ticks)]
    Queue[Channel of notification IDs]
    Worker[NotificationDispatcher]
    Delivery[TelegramNotifier / DryRunNotifier]
  end
  Simulator --> Ingest
  Producer --> Ingest
  Ingest --> Gate --> Engine
  Engine --> Cache --> DB
  Engine --> Rules
  Rules -->|ITransactionHistory| DB
  Engine -->|Atomic event / alerts / outbound rows| DB
  Engine -->|After commit| Queue
  Engine --> Hub --> Dashboard
  Dashboard --> Operator --> Gate
  Operator --> DB
  Queue --> Worker --> Delivery
  Worker <--> DB
  Delivery --> Telegram[Telegram HTTPS API]
```

Only ingestion authenticates requests in this demonstration. All other endpoints and hub access must be protected before real use. Full payout numbers are transformed to masks and hashes before persistence. Source-event responses exclude holder, hash, and suffix-index fields.

## Event and delivery sequence

```mermaid
sequenceDiagram
  participant C as Producer
  participant A as Ingestion API
  participant E as RuleEngine
  participant D as SQLite
  participant R as Rules
  participant H as SignalR
  participant W as Notification worker
  participant T as Telegram / dry-run
  C->>A: POST event + X-Api-Key
  A->>A: Validate identity, money, currency, date
  A->>E: ProcessAsync
  E->>E: Acquire process-wide gate
  E->>D: Check EventId
  alt Already persisted
    E-->>A: duplicate=true, alerts=[]
  else New event
    E->>D: Begin transaction; save protected event
    loop Each enabled rule
      E->>R: Evaluate with parameters and event-time window
      R->>D: Query history including current event
      R-->>E: Rule hits or isolated error
      E->>E: Merge active fingerprint / create alert
      E->>D: Save alert and Pending / Skipped outbound row
    end
    E->>D: Commit
    E->>W: Enqueue pending IDs
    E->>H: alertRaised / alertUpdated
    E-->>A: Event result and changed alert IDs
    A-->>C: HTTP 200
  end
  E->>E: Release gate
  W->>D: Read Pending row; save attempt count
  W->>T: Send snapshot text
  alt Success
    W->>D: Sent + SentAt
  else Failure
    loop Up to three retries
      W->>W: Wait 2/4/8 seconds or retry_after
      W->>T: Retry
    end
    W->>D: Sent or Failed
  end
```

Pending rows are loaded into the channel at startup. The transport can duplicate a message if the process stops after remote success but before recording it. A pending row with four persisted attempts becomes failed on recovery. An unexpected database failure leaves pending rows for restart; there is no periodic recovery scanner. Broadcast failures do not roll back committed evidence. Batch events use individual transactions and may partially complete if interrupted.

## Alert lifecycle

```mermaid
stateDiagram-v2
  [*] --> Open: First rule hit
  Open --> Open: Matching hit in rolling window / merge
  Open --> Acknowledged: Operator acknowledges
  Open --> Resolved: Operator resolves
  Acknowledged --> Acknowledged: Matching hit / merge
  Acknowledged --> Resolved: Operator resolves
  Resolved --> [*]
```

The same fingerprint after resolution or outside the deduplication window creates a new alert. Merging increments occurrence count, advances last-seen time, widens the evidence window, unions source event IDs, and retains the maximum severity. Notifications for merged alerts require a severity increase. Acknowledgment stores operator/time; resolution currently stores only status, so authenticated resolution audit metadata remains future work.

## Data model

```mermaid
erDiagram
  TransactionEvent {
    string EventId PK
    string MemberId
    string MemberName
    int Type
    long Amount "decimal x 10000"
    string Currency
    string PayoutBankCode
    string PayoutAccountNo "masked"
    string PayoutAccountHash "SHA-256 bank plus digits"
    string PayoutAccountSuffix "visible suffix, max six digits"
    string PayoutAccountHolder "private similarity input"
    long OccurredAt "UTC ticks"
    long ReceivedAt "UTC ticks"
  }
  Alert {
    guid Id PK
    string RuleCode
    int Severity
    string MemberId
    string Currency
    long Amount "nullable, decimal x 10000"
    string Fingerprint
    int OccurrenceCount
    long FirstSeenAt
    long LastSeenAt
    long WindowStart
    long WindowEnd
    string DetailsJson "safe aggregate evidence"
    string SourceEventIdsJson "logical event references"
    int Status
    string AcknowledgedBy
    long AcknowledgedAt
  }
  RuleSetting {
    string RuleCode PK
    bool Enabled
    string ParametersJson
    long UpdatedAt
  }
  NotificationSetting {
    string Currency PK "or default wildcard"
    bool Enabled
    int MinSeverity
    string ChatIdOverride
  }
  OutboundNotification {
    guid Id PK
    guid AlertId FK
    int Channel
    string Text "escaped snapshot"
    int Status
    int Attempts
    string Error "sanitized"
    long SentAt
    string SkipReason
  }
  Alert ||--o{ OutboundNotification : has
  TransactionEvent }o--o{ Alert : "logical IDs in JSON"
  RuleSetting ||--o{ Alert : "logical RuleCode"
  NotificationSetting ||--o{ OutboundNotification : "currency policy"
```

Only the outbound alert relationship is an enforced foreign key; evidence and configuration relationships are logical. Composite indexes cover member/type/time, masked payout/time, payout hash/time, suffix/time, and alert fingerprint/status. Monetary storage avoids floating point and SQLite TEXT comparisons. Shared-account evidence may span currencies; velocity and bonus calculations never add unlike currencies. The default shared-account mode is exact; suffix mode requires holder similarity of at least 0.85 and can generate false positives for human review. This service only alerts and never modifies member access or funds.
