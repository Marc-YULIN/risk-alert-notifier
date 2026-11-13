'use strict';
const $ = id => document.getElementById(id);
const state = { text: {}, locale: localStorage.getItem('locale') || 'en', page: 1, selected: null, connection: null, group: '', request: 0 };
const query = new URLSearchParams(location.search);
function node(tag, text, className) { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (className) e.className = className; return e; }
const t = key => state.text[key] || key;
const time = value => new Date(value).toLocaleString(state.locale, { hour12: false });
const money = a => a.amount == null ? '—' : `${a.currency} ${Number(a.amount).toLocaleString('en-US', { minimumFractionDigits: ['KRW', 'JPY'].includes(a.currency) ? 0 : 2, maximumFractionDigits: ['KRW', 'JPY'].includes(a.currency) ? 0 : 2 })}`;
async function api(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) { let detail; try { detail = await response.json(); } catch { detail = {}; } throw new Error(detail.title || `${t('error')} (${response.status})`); }
  return response.status === 204 ? null : response.json();
}
const maxToasts = 3; // bursts of alerts should not bury the dashboard
function toast(text, danger = false) { const box = $('toasts'), e = node('div', text, danger ? 'toast danger' : 'toast'); box.append(e); while (box.children.length > maxToasts) box.firstElementChild.remove(); setTimeout(() => e.remove(), 5000); }
function connectionStatus(connected) { $('connection').classList.toggle('live', connected); $('connection').querySelector('span').textContent = t(connected ? 'connected' : 'disconnected'); }
async function localeChanged() {
  state.locale = $('locale').value; localStorage.setItem('locale', state.locale);
  state.text = await api(`/api/i18n/${encodeURIComponent(state.locale)}`);
  document.documentElement.lang = state.locale; document.title = t('title');
  document.querySelectorAll('[data-i18n]').forEach(e => e.textContent = t(e.dataset.i18n));
  connectionStatus(state.connection?.state === 'Connected');
  await refresh(); if (state.selected) await openDetail(state.selected);
}
function filters() {
  const result = new URLSearchParams({ page: state.page, pageSize: 50 });
  ['currency', 'severity'].forEach(key => { if ($(key).value) result.set(key, $(key).value); });
  ['from', 'to'].forEach(key => { if ($(key).value) result.set(key, new Date($(key).value + 'Z').toISOString()); });
  return result;
}
async function loadAlerts(append = false, highlight = null) {
  if (!append) state.page = 1;
  const sequence = ++state.request;
  const data = await api(`/api/alerts?${filters()}`);
  if (sequence !== state.request) return;
  if (!append) $('alerts').replaceChildren();
  $('count').textContent = data.total.toLocaleString();
  if (!data.total) $('alerts').append(node('p', t('empty'), 'empty'));
  data.items.forEach(a => {
    const card = node('button', undefined, `alert-card ${a.severity.toLowerCase()}${a.id === highlight ? ' fresh' : ''}`);
    const top = node('div', undefined, 'card-top'); top.append(node('span', t(a.severity), `badge ${a.severity.toLowerCase()}`), node('span', a.currency, 'currency-tag'), node('time', time(a.lastSeenAt)));
    const row = node('div', undefined, 'card-main'); row.append(node('h3', t(a.ruleCode)), node('strong', money(a)));
    const bottom = node('div', undefined, 'card-bottom'); bottom.append(node('span', `${t('member')} ${a.memberId}`), node('span', `${t(a.status)} · ×${a.occurrenceCount}`));
    card.append(top, row, bottom); card.addEventListener('click', () => openDetail(a.id).catch(error)); $('alerts').append(card);
  });
  $('more').hidden = state.page * 50 >= data.total;
}
async function loadStats() {
  const stats = await api('/api/stats/summary?hours=24'); $('stats').replaceChildren(); $('breakdowns').replaceChildren();
  ['alerts', 'open', 'critical'].forEach((key, i) => { const e = node('div', undefined, 'stat'); e.append(node('span', t(key)), node('strong', [stats.total, stats.open, stats.critical][i].toLocaleString())); $('stats').append(e); });
  [stats.byRule, stats.bySeverity, stats.byCurrency].forEach(group => {
    const section = node('div', undefined, 'breakdown'); Object.entries(group).forEach(([key, value]) => { const row = node('div'); row.append(node('span', t(key)), node('b', value)); section.append(row); }); $('breakdowns').append(section);
  });
}
async function refresh(highlight) { await Promise.all([loadAlerts(false, highlight), loadStats()]); }
async function openDetail(id) {
  state.selected = id;
  const [data, notifications] = await Promise.all([api(`/api/alerts/${id}`), api(`/api/notifications?alertId=${id}`)]);
  if (state.selected !== id) return;
  const a = data.alert, content = $('detail-content'); content.replaceChildren();
  content.append(node('p', t('details'), 'eyebrow'), node('h2', t(a.ruleCode)), node('span', t(a.severity), `badge ${a.severity.toLowerCase()}`), node('p', t(a.ruleCode + '_description')));
  const dl = node('dl'); [['member', a.memberId], ['amount', money(a)], ['status', t(a.status)], ['window', `${time(a.windowStart)} → ${time(a.windowEnd)}`]].forEach(([key, value]) => dl.append(node('dt', t(key)), node('dd', value))); content.append(dl);
  content.append(node('pre', JSON.stringify(JSON.parse(a.detailsJson), null, 2), 'evidence'));
  const actions = node('div', undefined, 'actions');
  const by = node('input'); by.placeholder = t('operator'); by.setAttribute('aria-label', t('operator')); by.maxLength = 100;
  actions.append(by);
  ['acknowledge', 'resolve'].forEach(action => {
    const button = node('button', t(action), action === 'resolve' ? 'secondary' : 'primary');
    button.disabled = a.status === 'Resolved' || (action === 'acknowledge' && a.status === 'Acknowledged');
    button.addEventListener('click', async () => {
      if (!by.value.trim()) { by.focus(); return; }
      button.disabled = true;
      try { await api(`/api/alerts/${id}/${action}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ by: by.value.trim() }) }); await openDetail(id); await refresh(); }
      catch (e) { button.disabled = false; error(e); }
    }); actions.append(button);
  }); content.append(actions);
  content.append(node('h3', t('events')));
  data.sourceEvents.forEach(e => content.append(node('pre', JSON.stringify(e, null, 2), 'evidence')));
  content.append(node('h3', t('notifications')));
  notifications.forEach(n => { const entry = node('div', undefined, 'notification'); entry.append(node('strong', `${n.channel} · ${n.status} · ${n.attempts}`), node('p', n.skipReason || n.error || (n.sentAt ? time(n.sentAt) : '…')), node('pre', n.text)); content.append(entry); });
  $('detail').hidden = false; $('overlay').hidden = false;
  const url = new URL(location); url.searchParams.set('alert', id); history.replaceState(null, '', url);
}
function closeDetail() { state.selected = null; $('detail').hidden = true; $('overlay').hidden = true; const url = new URL(location); url.searchParams.delete('alert'); history.replaceState(null, '', url); }
async function updateGroups() {
  if (state.connection?.state !== 'Connected') return;
  if (state.group) await state.connection.invoke('LeaveCurrency', state.group);
  state.group = $('currency').value;
  if (state.group) await state.connection.invoke('JoinCurrency', state.group);
}
function error(e) { toast(e.message || t('error'), true); }
async function start() {
  $('locale').value = ['en', 'zh-TW', 'ko'].includes(state.locale) ? state.locale : 'en';
  ['from', 'to'].forEach(key => { const value = query.get(key); if (value && !Number.isNaN(Date.parse(value))) $(key).value = new Date(value).toISOString().slice(0, 19); });
  $('locale').addEventListener('change', () => localeChanged().catch(error));
  ['currency', 'severity', 'from', 'to'].forEach(id => $(id).addEventListener('change', async () => {
    try {
      if (id === 'currency') await updateGroups();
      const url = new URL(location); ['from', 'to'].forEach(key => { if ($(key).value) url.searchParams.set(key, new Date($(key).value + 'Z').toISOString()); else url.searchParams.delete(key); }); history.replaceState(null, '', url);
      await loadAlerts();
    } catch (e) { error(e); }
  }));
  $('more').addEventListener('click', () => { state.page++; loadAlerts(true).catch(error); });
  $('close').addEventListener('click', closeDetail); $('overlay').addEventListener('click', closeDetail);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeDetail(); });
  await localeChanged();
  if (query.get('alert')) await openDetail(query.get('alert'));
  if (window.signalR) {
    const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/alerts').withAutomaticReconnect().build(); state.connection = connection;
    const received = async a => { try { await refresh(a.id); toast(`${t(a.severity)} · ${t(a.ruleCode)} · ${a.memberId}`); if (state.selected === a.id) await openDetail(a.id); } catch (e) { error(e); } };
    connection.on('alertRaised', received); connection.on('alertUpdated', received);
    connection.onreconnecting(() => connectionStatus(false));
    connection.onreconnected(async () => { state.group = ''; await updateGroups(); connectionStatus(true); await refresh(); });
    const connect = async () => { try { await connection.start(); state.group = ''; await updateGroups(); connectionStatus(true); } catch { connectionStatus(false); setTimeout(connect, 5000); } };
    connection.onclose(() => { connectionStatus(false); setTimeout(connect, 5000); }); await connect();
  } else connectionStatus(false);
  setInterval(() => { refresh().catch(error); if (state.selected) openDetail(state.selected).catch(error); }, 15000);
}
start().catch(error);
