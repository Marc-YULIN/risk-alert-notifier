using System.Globalization;
using System.Text;

namespace RiskAlert.Core;

public static class MoneyFormatter
{
    public static string Format(decimal amount, string currency) => currency + " " +
        amount.ToString(currency is "KRW" or "JPY" ? "N0" : "N2", CultureInfo.InvariantCulture);
}
public static class TelegramMarkdown
{
    public static string Escape(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value)
        {
            if ("_*[]()~`>#+-=|{}.!\\".Contains(c)) result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }
}
public sealed record AlertText(string Title, string Body);
public static class AlertTextCatalog
{
    private static readonly Dictionary<string, Dictionary<string, string>> Locales = new()
    {
        ["en"] = new()
        {
            ["LARGE_TXN"] = "Large transaction", ["VELOCITY"] = "Transaction velocity",
            ["SHARED_PAYOUT_ACCOUNT"] = "Shared payout account", ["BONUS_RATIO"] = "High bonus ratio",
            ["Low"] = "Low", ["Medium"] = "Medium", ["High"] = "High", ["Critical"] = "Critical",
            ["LARGE_TXN_description"] = "The transaction exceeds the configured currency and type threshold.",
            ["VELOCITY_description"] = "The member exceeded the transaction count within the configured window.",
            ["SHARED_PAYOUT_ACCOUNT_description"] = "Multiple members used a matching payout account.",
            ["BONUS_RATIO_description"] = "The bonus total exceeds the configured share of deposits.",
            ["title"] = "Risk alert monitor", ["subtitle"] = "Live payment signals · Human decisions",
            ["all"] = "All", ["currency"] = "Currency", ["severity"] = "Severity", ["language"] = "Language",
            ["connected"] = "Live", ["disconnected"] = "Reconnecting", ["stats"] = "Last 24 hours",
            ["alerts"] = "Alerts", ["open"] = "Open", ["critical"] = "Critical", ["empty"] = "No alerts in this view",
            ["details"] = "Alert details", ["events"] = "Source events", ["notifications"] = "Notifications",
            ["window"] = "Evidence window", ["acknowledge"] = "Acknowledge", ["resolve"] = "Resolve",
            ["close"] = "Close", ["operator"] = "Operator name", ["loadMore"] = "Load more",
            ["error"] = "Request failed", ["member"] = "Member", ["amount"] = "Amount", ["status"] = "Status",
            ["Open"] = "Open", ["Acknowledged"] = "Acknowledged", ["Resolved"] = "Resolved",
            ["template"] = "Member: {0}\nAmount: {1}\n{2}"
        },
        ["zh-TW"] = new()
        {
            ["LARGE_TXN"] = "單筆大額交易", ["VELOCITY"] = "短時間高頻交易", ["SHARED_PAYOUT_ACCOUNT"] = "共用外部帳戶", ["BONUS_RATIO"] = "獎勵比例過高",
            ["Low"] = "低", ["Medium"] = "中", ["High"] = "高", ["Critical"] = "嚴重",
            ["LARGE_TXN_description"] = "交易金額超過該幣別與交易類型的設定門檻。",
            ["VELOCITY_description"] = "會員在設定時間內的交易次數達到門檻。",
            ["SHARED_PAYOUT_ACCOUNT_description"] = "多位會員使用相符的外部帳戶。",
            ["BONUS_RATIO_description"] = "獎勵總額占入帳總額的比例超過門檻。",
            ["title"] = "交易風險監控", ["subtitle"] = "即時交易訊號 · 人工判讀處置",
            ["all"] = "全部", ["currency"] = "幣別", ["severity"] = "嚴重度", ["language"] = "語系",
            ["connected"] = "即時連線", ["disconnected"] = "重新連線中", ["stats"] = "最近 24 小時",
            ["alerts"] = "告警", ["open"] = "待處理", ["critical"] = "嚴重", ["empty"] = "此篩選條件尚無告警",
            ["details"] = "告警詳情", ["events"] = "來源事件", ["notifications"] = "通知紀錄",
            ["window"] = "判斷時間範圍", ["acknowledge"] = "確認接手", ["resolve"] = "結案",
            ["close"] = "關閉", ["operator"] = "處理人員姓名", ["loadMore"] = "載入更多",
            ["error"] = "請求失敗", ["member"] = "會員", ["amount"] = "金額", ["status"] = "狀態",
            ["Open"] = "待處理", ["Acknowledged"] = "已接手", ["Resolved"] = "已結案",
            ["template"] = "會員：{0}\n金額：{1}\n{2}"
        },
        ["ko"] = new()
        {
            ["LARGE_TXN"] = "고액 거래", ["VELOCITY"] = "단시간 반복 거래", ["SHARED_PAYOUT_ACCOUNT"] = "공유 출금 계좌", ["BONUS_RATIO"] = "높은 보너스 비율",
            ["Low"] = "낮음", ["Medium"] = "보통", ["High"] = "높음", ["Critical"] = "심각",
            ["LARGE_TXN_description"] = "거래 금액이 통화 및 거래 유형별 기준을 초과했습니다.",
            ["VELOCITY_description"] = "설정된 시간 내 회원 거래 횟수가 기준에 도달했습니다.",
            ["SHARED_PAYOUT_ACCOUNT_description"] = "여러 회원이 일치하는 출금 계좌를 사용했습니다.",
            ["BONUS_RATIO_description"] = "입금 대비 보너스 총액 비율이 기준을 초과했습니다.",
            ["title"] = "거래 위험 모니터", ["subtitle"] = "실시간 거래 신호 · 사람의 판단",
            ["all"] = "전체", ["currency"] = "통화", ["severity"] = "심각도", ["language"] = "언어",
            ["connected"] = "연결됨", ["disconnected"] = "재연결 중", ["stats"] = "최근 24시간",
            ["alerts"] = "알림", ["open"] = "미처리", ["critical"] = "심각", ["empty"] = "해당 알림이 없습니다",
            ["details"] = "알림 상세", ["events"] = "관련 거래", ["notifications"] = "발송 기록",
            ["window"] = "분석 시간 범위", ["acknowledge"] = "담당 확인", ["resolve"] = "해결",
            ["close"] = "닫기", ["operator"] = "담당자 이름", ["loadMore"] = "더 보기",
            ["error"] = "요청 실패", ["member"] = "회원", ["amount"] = "금액", ["status"] = "상태",
            ["Open"] = "미처리", ["Acknowledged"] = "확인됨", ["Resolved"] = "해결됨",
            ["template"] = "회원: {0}\n금액: {1}\n{2}"
        }
    };
    public static IReadOnlyDictionary<string, string> Get(string locale) => Locales.GetValueOrDefault(locale) ?? Locales["en"];
    public static AlertText Render(Alert alert, string locale)
    {
        var text = Get(locale);
        return new(text[alert.RuleCode] + " · " + text[alert.Severity.ToString()],
            string.Format(CultureInfo.InvariantCulture, text["template"], alert.MemberId,
                alert.Amount is decimal amount ? MoneyFormatter.Format(amount, alert.Currency) : "—", text[alert.RuleCode + "_description"]));
    }
}

public sealed record NotificationDecision(bool Send, string? Reason, NotificationSetting? Setting);
public static class NotificationPolicy
{
    public static NotificationDecision ShouldSend(Alert alert, IEnumerable<NotificationSetting> settings,
        bool isNew = true, Severity? previousSeverity = null)
    {
        var all = settings.ToArray();
        var setting = all.FirstOrDefault(x => x.Currency == alert.Currency) ?? all.FirstOrDefault(x => x.Currency == "*");
        if (setting is null || !setting.Enabled) return new(false, "CurrencyDisabled", setting);
        if (alert.Severity < setting.MinSeverity) return new(false, "BelowMinimumSeverity", setting);
        if (!isNew && (previousSeverity is null || alert.Severity <= previousSeverity)) return new(false, "MergedWithoutEscalation", setting);
        return new(true, null, setting);
    }
}
