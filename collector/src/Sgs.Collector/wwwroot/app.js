(function () {
  'use strict';

  // ---------- 基础工具 ----------
  function h(tag, props) {
    var node = document.createElement(tag);
    if (props) {
      Object.keys(props).forEach(function (k) {
        var v = props[k];
        if (v === null || v === undefined || v === false) return;
        if (k === 'class') node.className = v;
        else if (k === 'text') node.textContent = v;
        else if (k.indexOf('on') === 0) node.addEventListener(k.slice(2), v);
        else if (k === 'value' || k === 'checked' || k === 'disabled' || k === 'selected') node[k] = v;
        else node.setAttribute(k, v === true ? '' : v);
      });
    }
    for (var i = 2; i < arguments.length; i++) append(node, arguments[i]);
    return node;
  }
  function append(node, c) {
    if (c === null || c === undefined || c === false) return;
    if (Array.isArray(c)) { c.forEach(function (x) { append(node, x); }); return; }
    node.appendChild(typeof c === 'object' ? c : document.createTextNode(String(c)));
  }
  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); return node; }
  function setChildren(node) {
    clear(node);
    for (var i = 1; i < arguments.length; i++) append(node, arguments[i]);
    return node;
  }

  function api(method, path, body) {
    var opt = { method: method, headers: {} };
    if (method !== 'GET') {
      opt.headers['X-Requested-With'] = 'sgs-collector';
      opt.headers['Content-Type'] = 'application/json';
      opt.body = JSON.stringify(body === undefined ? {} : body);
    }
    return fetch(path, opt).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        if (text) { try { data = JSON.parse(text); } catch (e) { data = null; } }
        if (!res.ok) {
          var msg = data && typeof data.error === 'string' && data.error ? data.error
            : (data && typeof data.title === 'string' && data.title ? data.title : '请求失败（HTTP ' + res.status + '）');
          var err = new Error(msg);
          err.status = res.status;
          throw err;
        }
        return data;
      });
    });
  }
  function qs(obj) {
    return Object.keys(obj).filter(function (k) { return obj[k] !== '' && obj[k] !== null && obj[k] !== undefined; })
      .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(obj[k]); }).join('&');
  }

  var toastTimer = null;
  function toast(msg, isErr) {
    var t = document.getElementById('toast');
    t.textContent = msg;
    t.className = 'toast' + (isErr ? ' err' : '');
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.hidden = true; }, isErr ? 6000 : 2500);
  }
  function fail(e) { toast(e && e.message ? e.message : String(e), true); }

  function confirmDialog(text, okText) {
    return new Promise(function (resolve) {
      var mask;
      function close(v) { document.body.removeChild(mask); resolve(v); }
      mask = h('div', { class: 'dialog-mask' },
        h('div', { class: 'dialog', role: 'dialog' },
          h('p', { text: text }),
          h('div', { class: 'row' },
            h('button', { class: 'btn', type: 'button', text: '取消', onclick: function () { close(false); } }),
            h('button', { class: 'btn primary', type: 'button', text: okText || '确定', onclick: function () { close(true); } }))));
      document.body.appendChild(mask);
    });
  }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }
  function fmtTime(iso) {
    if (!iso) return '-';
    var d = new Date(iso);
    if (isNaN(d.getTime())) return String(iso);
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' +
      pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }
  function fmtDur(ms) {
    if (ms === null || ms === undefined || isNaN(ms) || ms < 0) return '-';
    var s = Math.floor(ms / 1000);
    if (s < 60) return s + ' 秒';
    var m = Math.floor(s / 60);
    if (m < 60) return m + ' 分 ' + (s % 60) + ' 秒';
    return Math.floor(m / 60) + ' 时 ' + (m % 60) + ' 分';
  }
  function between(a, b) {
    if (!a) return '-';
    var end = b ? new Date(b).getTime() : Date.now();
    return fmtDur(end - new Date(a).getTime());
  }
  function fmtBytes(n) {
    if (n === null || n === undefined) return '-';
    var u = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0, v = Number(n);
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return (i === 0 ? v : v.toFixed(v >= 100 ? 0 : 2)) + ' ' + u[i];
  }
  function num(n) { return n === null || n === undefined ? '-' : Number(n).toLocaleString('zh-CN'); }

  var STATE = { idle: '空闲', running: '运行中', cancelling: '终止中', syncing: '同步中', restoring: '恢复中' };
  var STATE_CLS = { idle: '', running: 'info', cancelling: 'warn', syncing: 'info', restoring: 'info' };
  var PHASE = { bootstrap: '初始化', crawling: '抓取中', syncing: '同步中', done: '已结束' };
  var TRIGGER = { manual: '手动', schedule: '定时', restore: '恢复' };
  var RUN_STATUS = { running: '运行中', completed: '已完成', cancelled: '已终止', failed: '失败' };
  var RUN_CLS = { running: 'info', completed: 'ok', cancelled: 'warn', failed: 'err' };
  var SYNC_STATUS = { skipped: '已跳过', ok: '成功', failed: '失败', cancelled: '已终止' };
  var SYNC_CLS = { skipped: '', ok: 'ok', failed: 'err', cancelled: 'warn' };
  var OUTCOME = { not_modified: '未修改（304）', unchanged: '内容未变', new_version: '新版本', failed: '失败', ok: '成功' };
  var OUTCOME_CLS = { failed: 'err', new_version: 'ok' };
  var STAT_LABELS = [
    ['discovered', '发现'], ['outOfScope', '超出范围'], ['completed', '已处理'], ['notModified', '未修改（304）'],
    ['unchanged', '内容未变'], ['newVersions', '新版本'], ['memberNewVersions', '成员新版本'],
    ['memberUnchanged', '成员未变'], ['memberFailed', '成员失败'], ['failed', '失败（有历史）'],
    ['neverSucceeded', '从未成功（跳过）'], ['cancelled', '已取消'], ['discoverOnly', '仅发现'],
    ['limitSkipped', '超限跳过'], ['bytesDownloaded', '已下载字节']
  ];

  function badge(text, cls) { return h('span', { class: 'badge' + (cls ? ' ' + cls : ''), text: text }); }
  function outcomeBadge(o) {
    if (!o) return h('span', { class: 'muted', text: '-' });
    return badge(OUTCOME[o] || o, OUTCOME_CLS[o] || '');
  }
  function empty(text) { return h('div', { class: 'empty', text: text || '暂无数据' }); }
  function kv(pairs) {
    return h('div', { class: 'kv' }, pairs.map(function (p) {
      return h('div', null, h('span', { class: 'k', text: p[0] }),
        h('span', { class: 'v' }, p[1] === null || p[1] === undefined || p[1] === '' ? '-' : p[1]));
    }));
  }
  function tableOf(headCols, rows) {
    return h('div', { class: 'tablewrap' }, h('table', null,
      h('thead', null, h('tr', null, headCols.map(function (c) { return h('th', { text: c }); }))),
      h('tbody', null, rows)));
  }
  function pager(total, offset, limit, onChange) {
    var page = Math.floor(offset / limit) + 1, pages = Math.max(1, Math.ceil(total / limit));
    return h('div', { class: 'pager' },
      h('span', { text: '共 ' + num(total) + ' 条，第 ' + page + ' / ' + pages + ' 页' }),
      h('button', { class: 'btn', type: 'button', text: '上一页', disabled: offset <= 0, onclick: function () { onChange(Math.max(0, offset - limit)); } }),
      h('button', { class: 'btn', type: 'button', text: '下一页', disabled: offset + limit >= total, onclick: function () { onChange(offset + limit); } }));
  }
  function errBox(e) { return h('div', { class: 'err-box', text: e && e.message ? e.message : String(e) }); }

  // ---------- 标签页 ----------
  var tabs = {};
  function showTab(name) {
    Array.prototype.forEach.call(document.querySelectorAll('#tabs button'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-tab') === name);
    });
    ['progress', 'resources', 'runs', 'rules'].forEach(function (n) {
      document.getElementById('tab-' + n).hidden = n !== name;
    });
    if (tabs[name] && tabs[name].show) tabs[name].show();
  }

  // ---------- 进度 ----------
  var P = { root: null, refs: {}, status: null, timer: null, built: false };

  function statusInterval() { return P.status && P.status.state !== 'idle' ? 1000 : 5000; }

  function pollStatus() {
    clearTimeout(P.timer);
    api('GET', '/api/status').then(function (s) {
      P.status = s;
      setConn(true);
      renderProgress();
    }).catch(function () { setConn(false); }).then(function () {
      P.timer = setTimeout(pollStatus, statusInterval());
    });
  }
  function setConn(ok) {
    var c = document.getElementById('conn');
    c.textContent = ok ? '已连接' : '连接中断';
    c.className = 'conn' + (ok ? '' : ' bad');
  }
  function pollSoon() { clearTimeout(P.timer); P.timer = setTimeout(pollStatus, 200); }

  function action(path, okMsg) {
    return api('POST', path).then(function () { toast(okMsg); pollSoon(); }).catch(function (e) { fail(e); pollSoon(); });
  }

  function buildProgress() {
    var r = P.refs, root = P.root;
    r.state = h('span', { class: 'badge state' });
    r.btnStart = h('button', { class: 'btn primary', type: 'button', text: '开始收集', onclick: function () { action('/api/runs', '已开始收集'); } });
    r.btnCancel = h('button', { class: 'btn danger', type: 'button', text: '终止', onclick: function () { action('/api/runs/current/cancel', '已请求终止'); } });
    r.btnSync = h('button', { class: 'btn', type: 'button', text: '立即同步', onclick: function () { action('/api/sync', '已开始同步'); } });
    r.btnFull = h('button', { class: 'btn', type: 'button', text: '全量重新同步', onclick: function () {
      confirmDialog('会把全部版本重新推送一遍，不会删除 Worker 中的数据。确定继续？', '全量重新同步').then(function (ok) {
        if (ok) action('/api/sync/full', '已开始全量同步');
      });
    } });
    r.btnRestore = h('button', { class: 'btn', type: 'button', text: '从 Worker 恢复', onclick: function () {
      confirmDialog('此操作仅用于本地数据库丢失后，从 Worker 恢复目录信息。恢复后的首轮收集会完整下载一遍全部资源。确定继续？', '开始恢复').then(function (ok) {
        if (ok) action('/api/restore', '已开始恢复');
      });
    } });
    r.info = h('div');
    r.manual = h('div');
    r.runTitle = h('h3');
    r.bar = h('i');
    r.barText = h('span', { class: 'muted small' });
    r.counts = h('div', { class: 'counts' });
    r.sync = h('div', { class: 'muted' });
    r.inflight = h('ul', { class: 'list' });
    r.warns = h('ul', { class: 'list' });
    r.events = h('ul', { class: 'list' });

    setChildren(root,
      h('div', { class: 'card' },
        h('div', { class: 'row between' },
          h('div', { class: 'row' }, h('h2', { text: '运行状态' }), r.state),
          h('div', { class: 'row' }, r.btnStart, r.btnCancel, r.btnSync, r.btnFull, r.btnRestore)),
        h('div', { style: 'margin-top:10px' }, r.info),
        r.manual),
      h('div', { class: 'card' }, r.runTitle,
        h('div', { class: 'bar' }, r.bar), h('div', { style: 'margin:4px 0 10px' }, r.barText),
        r.counts, h('div', { style: 'margin-top:10px' }, r.sync)),
      h('div', { class: 'card' }, h('h3', { text: '进行中' }), r.inflight),
      h('div', { class: 'card' }, h('h3', { text: '警告' }), r.warns),
      h('div', { class: 'card' }, h('h3', { text: '最近事件（新在上）' }), r.events));
    P.built = true;
  }

  function fillList(ul, items, build, emptyText) {
    var top = ul.scrollTop;
    clear(ul);
    if (!items.length) ul.appendChild(h('li', { class: 'muted', text: emptyText }));
    items.forEach(function (it) { ul.appendChild(build(it)); });
    ul.scrollTop = top;
  }

  function nextText(s) {
    if (s.nextScheduledAt) return fmtTime(s.nextScheduledAt);
    return s.intervalMinutes > 0 ? '首次需手动启动' : '仅手动';
  }

  function renderProgress() {
    if (!P.built) buildProgress();
    var s = P.status, r = P.refs;
    if (!s) return;
    var idle = s.state === 'idle';
    r.state.textContent = STATE[s.state] || s.state;
    r.state.className = 'badge state ' + (STATE_CLS[s.state] || '');
    r.btnStart.disabled = !idle;
    r.btnCancel.disabled = !(s.state === 'running' || s.state === 'syncing' || s.state === 'restoring');
    r.btnSync.disabled = !idle;
    r.btnFull.disabled = !idle;
    r.btnRestore.disabled = !idle;

    var cur = s.current;
    setChildren(r.info, kv([
      ['当前轮次', cur ? '#' + cur.runId : '-'],
      ['触发方式', cur ? (TRIGGER[cur.trigger] || cur.trigger) : '-'],
      ['开始时间', cur ? fmtTime(cur.startedAt) : '-'],
      ['阶段', cur ? (PHASE[cur.phase] || cur.phase) : '-'],
      ['已耗时', cur ? between(cur.startedAt) : '-'],
      ['下次计划', nextText(s)],
      ['待同步版本数', num(s.pendingSync)],
      ['本地版本数', num(s.localVersions)],
      ['Worker', s.workerBaseUrl]
    ]));

    var mt = s.manualTask;
    setChildren(r.manual);
    if (mt) {
      var txt = (mt.kind === 'sync' ? '同步' : mt.kind === 'full-sync' ? '全量同步' : mt.kind === 'restore' ? '恢复' : mt.kind);
      var prog = !mt.finished && mt.total ? '（' + num(mt.done) + ' / ' + num(mt.total) + '）' : '';
      r.manual.appendChild(h('div', { class: 'note', style: 'margin-top:10px',
        text: '最近手动任务：' + txt + (mt.finished ? '（已结束）' : '（进行中）') + prog + (mt.message ? ' - ' + mt.message : '') }));
    }

    var live = cur || s.last;
    var isLast = !cur && !!s.last;
    if (!live) {
      r.runTitle.textContent = '收集进度';
      r.bar.style.width = '0%';
      r.barText.textContent = '尚无收集记录';
      setChildren(r.counts);
      r.sync.textContent = '';
      fillList(r.inflight, [], null, '无');
      fillList(r.warns, [], null, '无');
      fillList(r.events, [], null, '无');
      return;
    }
    var st = live.stats || {};
    r.runTitle.textContent = (isLast ? '最近一轮（#' : '当前轮次（#') + live.runId + '）';
    var pct = st.discovered > 0 ? Math.min(100, st.completed * 100 / st.discovered) : 0;
    r.bar.style.width = pct.toFixed(1) + '%';
    r.barText.textContent = num(st.completed) + ' / ' + num(st.discovered) + '（' + pct.toFixed(1) + '%）';
    var cells = [
      ['发现', num(st.discovered)], ['超出范围', num(st.outOfScope)], ['未修改（304）', num(st.notModified)],
      ['内容未变', num(st.unchanged)], ['新版本', num(st.newVersions)], ['成员新版本', num(st.memberNewVersions)],
      ['成员未变', num(st.memberUnchanged)], ['成员失败', num(st.memberFailed)], ['失败（有历史）', num(st.failed)],
      ['从未成功（跳过）', num(st.neverSucceeded)], ['已下载', fmtBytes(st.bytesDownloaded)], ['队列长度', num(live.queueLength)]
    ];
    setChildren(r.counts, cells.map(function (c) {
      return h('div', { class: 'count' }, h('b', { text: c[1] }), h('span', { text: c[0] }));
    }));
    r.sync.textContent = '同步进度：' + num(live.syncDone) + ' / ' + num(live.syncTotal);
    fillList(r.inflight, live.inFlight || [], function (it) {
      return h('li', null, h('span', { class: 't', text: between(it.startedAt) }), h('span', { text: it.key }));
    }, '当前没有进行中的项目');
    fillList(r.warns, st.warnings || [], function (w) { return h('li', { class: 'ev-warn', text: w }); }, '无警告');
    fillList(r.events, live.events || [], function (e) {
      return h('li', { class: 'ev-' + e.level }, h('span', { class: 't', text: fmtTime(e.at).slice(11) }), h('span', { text: e.message }));
    }, '无事件');
  }

  tabs.progress = { show: function () { renderProgress(); } };

  // ---------- 资源 ----------
  var R = { q: '', status: 'all', offset: 0, limit: 100, root: null, built: false, list: null, detail: null };

  function backToList() { R.detail.hidden = true; R.list.hidden = false; loadResources(); }

  function buildResources() {
    function doSearch() { R.q = R.qInput.value.trim(); R.offset = 0; loadResources(); }
    R.qInput = h('input', { type: 'search', placeholder: '按 key 子串搜索', class: 'grow',
      onkeydown: function (e) { if (e.key === 'Enter') doSearch(); } });
    R.sel = h('select', { onchange: function () { R.status = R.sel.value; R.offset = 0; loadResources(); } },
      [['all', '全部'], ['ok', '正常'], ['failed', '失败'], ['never', '从未成功'], ['multi', '多版本']].map(function (o) {
        return h('option', { value: o[0], text: o[1] });
      }));
    R.listBox = h('div');
    R.list = h('div', { class: 'stack' },
      h('div', { class: 'card row' }, R.qInput, R.sel, h('button', { class: 'btn primary', type: 'button', text: '搜索', onclick: doSearch })),
      R.listBox);
    R.detail = h('div', { class: 'stack', hidden: true });
    setChildren(R.root, R.list, R.detail);
    R.built = true;
  }

  function loadResources() {
    api('GET', '/api/resources?' + qs({ q: R.q, status: R.status, offset: R.offset, limit: R.limit })).then(function (d) {
      var rows = d.items.map(function (it) {
        return h('tr', { class: 'clickable', onclick: function () { openResource(it.id); } },
          h('td', { class: 'key mono', text: it.key }),
          h('td', { class: 'nw', text: it.latestVersion > 0 ? 'v' + it.latestVersion : '-' }),
          h('td', null, outcomeBadge(it.lastOutcome)),
          h('td', { text: it.lastHttpStatus === null || it.lastHttpStatus === undefined ? '-' : it.lastHttpStatus }),
          h('td', { class: 'nw', text: fmtTime(it.lastCheckedAt) }),
          h('td', { class: 'nw', text: fmtTime(it.lastSuccessAt) }));
      });
      setChildren(R.listBox,
        d.items.length ? tableOf(['key', '最新版本', '最后结果', 'HTTP 状态', '最后检查', '最后成功'], rows) : empty('没有符合条件的资源'),
        pager(d.total, R.offset, R.limit, function (o) { R.offset = o; loadResources(); }));
    }).catch(function (e) { setChildren(R.listBox, errBox(e)); });
  }

  function openResource(id) {
    R.list.hidden = true;
    R.detail.hidden = false;
    setChildren(R.detail, empty('加载中...'));
    api('GET', '/api/resources/' + id).then(function (d) {
      var res = d.resource;
      var back = h('button', { class: 'btn', type: 'button', text: '返回列表', onclick: backToList });
      var parent = d.parentKey ? h('button', { class: 'link', type: 'button', text: d.parentKey, onclick: function () { openResource(res.parentId); } }) : null;
      var versionRows = d.versions.map(function (v) {
        return h('tr', null,
          h('td', { class: 'nw', text: 'v' + v.version }),
          h('td', { class: 'nw', text: fmtBytes(v.size) }),
          h('td', { text: v.contentType || '-' }),
          h('td', { class: 'nw', text: fmtTime(v.collectedAt) }),
          h('td', { class: 'nw', text: '#' + v.runId }),
          h('td', null, v.synced ? badge('已同步', 'ok') : badge('未同步', 'warn')),
          h('td', { class: 'mono', text: v.hash ? String(v.hash).slice(0, 12) : '-' }),
          h('td', null, v.url ? h('a', { href: v.url, target: '_blank', rel: 'noopener', text: 'Worker 链接' }) : '-'));
      });
      var memberRows = d.members.map(function (m) {
        return h('tr', { class: 'clickable', onclick: function () { openResource(m.id); } },
          h('td', { class: 'key mono', text: m.key }),
          h('td', { class: 'nw', text: m.latestVersion > 0 ? 'v' + m.latestVersion : '-' }),
          h('td', null, outcomeBadge(m.lastOutcome)));
      });
      var failRows = d.failures.map(function (f) {
        return h('tr', null,
          h('td', { class: 'nw', text: fmtTime(f.occurredAt) }),
          h('td', { class: 'nw', text: '#' + f.runId }),
          h('td', { text: f.httpStatus === null || f.httpStatus === undefined ? '-' : f.httpStatus }),
          h('td', null, f.hadHistory ? badge('有历史', 'warn') : badge('从未成功', 'err')),
          h('td', { text: f.error || '-' }));
      });
      setChildren(R.detail,
        h('div', { class: 'row between' }, h('h2', { class: 'mono grow', text: res.key }), back),
        h('div', { class: 'card' }, h('h3', { text: '基本信息' }), kv([
          ['ID', res.id],
          ['上级包', parent],
          ['成员原始路径', res.sourcePath],
          ['抓取地址', res.fetchUrl],
          ['最新版本', res.latestVersion > 0 ? 'v' + res.latestVersion : '-'],
          ['最新地址', d.latestUrl ? h('a', { href: d.latestUrl, target: '_blank', rel: 'noopener', text: d.latestUrl }) : null],
          ['最后结果', outcomeBadge(res.lastOutcome)],
          ['最后 HTTP 状态', res.lastHttpStatus],
          ['最后检查', fmtTime(res.lastCheckedAt)],
          ['最后成功', fmtTime(res.lastSuccessAt)],
          ['首次发现', fmtTime(res.firstSeenAt)],
          ['最近出现轮次', res.lastSeenRun ? '#' + res.lastSeenRun : null],
          ['ETag', res.etag],
          ['Last-Modified', res.lastModified],
          ['哈希', res.latestHash],
          ['处理版本', res.processingRev],
          ['最后错误', res.lastError]
        ])),
        h('div', null, h('h3', { text: '版本' }),
          d.versions.length ? tableOf(['版本', '大小', '类型', '收集时间', '轮次', '同步', '哈希', '链接'], versionRows) : empty('尚无版本')),
        h('div', null, h('h3', { text: '子成员（' + d.members.length + '）' }),
          d.members.length ? tableOf(['key', '最新版本', '最后结果'], memberRows) : empty('无子成员')),
        h('div', null, h('h3', { text: '最近失败' }),
          d.failures.length ? tableOf(['时间', '轮次', 'HTTP', '类型', '错误'], failRows) : empty('无失败记录')));
    }).catch(function (e) {
      setChildren(R.detail, errBox(e), h('div', null, h('button', { class: 'btn', type: 'button', text: '返回列表', onclick: backToList })));
    });
  }

  tabs.resources = { show: function () {
    if (!R.built) buildResources();
    if (!R.list.hidden) loadResources();
  } };

  function gotoResource(id) {
    showTab('resources');
    openResource(id);
  }

  // ---------- 收集记录 ----------
  var N = { offset: 0, limit: 50, built: false, listBox: null, list: null, detail: null };

  function runMainCounts(r) {
    var s = r.stats;
    if (!s) return '-';
    if (s.restored !== undefined && s.discovered === undefined) return '恢复 ' + num(s.restored) + ' 条';
    return '发现 ' + num(s.discovered) + ' / 新版本 ' + num(s.newVersions) + ' / 失败 ' + num((s.failed || 0) + (s.neverSucceeded || 0));
  }

  function loadRuns() {
    if (!N.built) {
      N.listBox = h('div');
      N.list = h('div', { class: 'stack' },
        h('div', { class: 'row between' }, h('h2', { text: '收集记录' }),
          h('button', { class: 'btn', type: 'button', text: '刷新', onclick: loadRuns })),
        N.listBox);
      N.detail = h('div', { class: 'stack', hidden: true });
      setChildren(document.getElementById('tab-runs'), N.list, N.detail);
      N.built = true;
    }
    if (N.list.hidden) return;
    api('GET', '/api/runs?' + qs({ offset: N.offset, limit: N.limit })).then(function (d) {
      var rows = d.items.map(function (r) {
        return h('tr', { class: 'clickable', onclick: function () { openRun(r.id); } },
          h('td', { class: 'nw', text: '#' + r.id }),
          h('td', { class: 'nw', text: TRIGGER[r.trigger] || r.trigger }),
          h('td', null, badge(RUN_STATUS[r.status] || r.status, RUN_CLS[r.status] || '')),
          h('td', { class: 'nw', text: fmtTime(r.startedAt) }),
          h('td', { class: 'nw', text: fmtTime(r.finishedAt) }),
          h('td', { class: 'nw', text: between(r.startedAt, r.finishedAt) }),
          h('td', { text: runMainCounts(r) }),
          h('td', null, r.syncStatus ? badge(SYNC_STATUS[r.syncStatus] || r.syncStatus, SYNC_CLS[r.syncStatus] || '') : '-'),
          h('td', { text: r.syncError || r.error || '' }));
      });
      setChildren(N.listBox,
        d.items.length ? tableOf(['ID', '触发', '状态', '开始', '结束', '耗时', '主要计数', '同步', '错误'], rows) : empty('暂无收集记录'),
        pager(d.total, N.offset, N.limit, function (o) { N.offset = o; loadRuns(); }));
    }).catch(function (e) { setChildren(N.listBox, errBox(e)); });
  }

  function openRun(id) {
    N.list.hidden = true;
    N.detail.hidden = false;
    var D = { vOff: 0, fOff: 0, filter: 'all', limit: 50 };
    var vBox = h('div'), fBox = h('div'), head = h('div', { class: 'stack' });
    var back = h('button', { class: 'btn', type: 'button', text: '返回列表', onclick: function () {
      N.detail.hidden = true; N.list.hidden = false; loadRuns();
    } });
    var fSel = h('select', { onchange: function () { D.filter = fSel.value; D.fOff = 0; loadFailures(); } },
      [['all', '全部'], ['history', '有历史'], ['never', '从未成功']].map(function (o) { return h('option', { value: o[0], text: o[1] }); }));
    setChildren(N.detail, head,
      h('div', null, h('h3', { text: '本轮新版本' }), vBox),
      h('div', null, h('div', { class: 'row between' }, h('h3', { text: '本轮失败' }), fSel), fBox));

    function loadVersions() {
      api('GET', '/api/runs/' + id + '/versions?' + qs({ offset: D.vOff, limit: D.limit })).then(function (d) {
        var rows = d.items.map(function (v) {
          return h('tr', null,
            h('td', { class: 'key mono' }, h('button', { class: 'link', type: 'button', text: v.key, onclick: function () { gotoResource(v.resourceId); } })),
            h('td', { class: 'nw', text: 'v' + v.version }),
            h('td', { class: 'nw', text: fmtBytes(v.size) }),
            h('td', { text: v.contentType || '-' }),
            h('td', { class: 'nw', text: fmtTime(v.collectedAt) }));
        });
        setChildren(vBox, d.items.length ? tableOf(['key', '版本', '大小', '类型', '收集时间'], rows) : empty('本轮没有新版本'),
          pager(d.total, D.vOff, D.limit, function (o) { D.vOff = o; loadVersions(); }));
      }).catch(function (e) { setChildren(vBox, errBox(e)); });
    }
    function loadFailures() {
      api('GET', '/api/runs/' + id + '/failures?' + qs({ filter: D.filter, offset: D.fOff, limit: D.limit })).then(function (d) {
        var rows = d.items.map(function (f) {
          return h('tr', null,
            h('td', { class: 'key mono' }, h('button', { class: 'link', type: 'button', text: f.key, onclick: function () { gotoResource(f.resourceId); } })),
            h('td', { text: f.httpStatus === null || f.httpStatus === undefined ? '-' : f.httpStatus }),
            h('td', null, f.hadHistory ? badge('有历史', 'warn') : badge('从未成功', 'err')),
            h('td', { class: 'nw', text: fmtTime(f.occurredAt) }),
            h('td', { text: f.error || '-' }));
        });
        setChildren(fBox, d.items.length ? tableOf(['key', 'HTTP', '类型', '时间', '错误'], rows) : empty('没有失败记录'),
          pager(d.total, D.fOff, D.limit, function (o) { D.fOff = o; loadFailures(); }));
      }).catch(function (e) { setChildren(fBox, errBox(e)); });
    }

    api('GET', '/api/runs/' + id).then(function (r) {
      var s = r.stats || {};
      var statCells = [];
      if (r.stats && s.restored !== undefined && s.discovered === undefined) {
        statCells.push(h('div', { class: 'count' }, h('b', { text: num(s.restored) }), h('span', { text: '恢复条数' })));
      } else {
        STAT_LABELS.forEach(function (p) {
          if (s[p[0]] === undefined) return;
          statCells.push(h('div', { class: 'count' }, h('b', { text: p[0] === 'bytesDownloaded' ? fmtBytes(s[p[0]]) : num(s[p[0]]) }), h('span', { text: p[1] })));
        });
      }
      var warns = Array.isArray(s.warnings) ? s.warnings : [];
      setChildren(head,
        h('div', { class: 'row between' }, h('h2', { text: '收集轮次 #' + r.id }), back),
        h('div', { class: 'card' }, kv([
          ['触发', TRIGGER[r.trigger] || r.trigger],
          ['状态', badge(RUN_STATUS[r.status] || r.status, RUN_CLS[r.status] || '')],
          ['开始', fmtTime(r.startedAt)],
          ['结束', fmtTime(r.finishedAt)],
          ['耗时', between(r.startedAt, r.finishedAt)],
          ['同步状态', r.syncStatus ? badge(SYNC_STATUS[r.syncStatus] || r.syncStatus, SYNC_CLS[r.syncStatus] || '') : null]
        ]), r.error ? h('div', { class: 'err-box', style: 'margin-top:8px', text: '错误：' + r.error }) : null,
          r.syncError ? h('div', { class: 'err-box', style: 'margin-top:8px', text: '同步错误：' + r.syncError }) : null),
        h('div', { class: 'card' }, h('h3', { text: '完整统计' }),
          statCells.length ? h('div', { class: 'counts' }, statCells) : h('span', { class: 'muted', text: '无统计数据' })),
        h('div', { class: 'card' }, h('h3', { text: '警告（' + warns.length + '）' }),
          h('ul', { class: 'list' }, warns.length ? warns.map(function (w) { return h('li', { class: 'ev-warn', text: w }); }) : h('li', { class: 'muted', text: '无警告' }))));
    }).catch(function (e) { setChildren(head, errBox(e), back); });
    loadVersions();
    loadFailures();
  }

  tabs.runs = { show: loadRuns };

  // ---------- Header 规则 ----------
  var H = { built: false, listBox: null, formBox: null };

  function ruleBody(rule, enabled) {
    return { pattern: rule.pattern, headers: rule.headers.map(function (x) { return { name: x.name, value: x.value }; }),
      enabled: enabled, note: rule.note || '' };
  }

  function buildRules() {
    H.listBox = h('div');
    H.formBox = h('div');
    var urlInput = h('input', { type: 'text', class: 'grow', placeholder: '输入完整 URL 或 key',
      onkeydown: function (e) { if (e.key === 'Enter') runPreview(); } });
    var previewOut = h('div', { style: 'margin-top:10px' });
    function runPreview() {
      var u = urlInput.value.trim();
      if (!u) { toast('请输入 URL 或 key', true); return; }
      api('GET', '/api/header-rules/preview?' + qs({ url: u })).then(function (d) {
        var rows = d.headers.map(function (x) {
          return h('tr', null, h('td', { class: 'mono', text: x.name }), h('td', { class: 'mono', text: x.value === '' ? '（空）' : x.value }),
            h('td', { class: 'nw', text: x.source === 'default' ? '默认' : String(x.source).replace('rule:', '规则 #') }));
        });
        setChildren(previewOut,
          kv([['key', d.key], ['是否在抓取范围内', d.inScope ? '是' : '否'],
            ['命中规则（按应用顺序）', d.matchedRules.length ? d.matchedRules.map(function (m) { return '#' + m.id + ' ' + m.pattern; }).join(' -> ') : '无']]),
          h('div', { style: 'margin-top:8px' }, d.headers.length ? tableOf(['请求头', '值', '来源'], rows) : empty('没有请求头')));
      }).catch(function (e) { setChildren(previewOut, errBox(e)); });
    }
    setChildren(document.getElementById('tab-rules'),
      h('div', { class: 'row between' }, h('h2', { text: 'Header 规则' }),
        h('button', { class: 'btn primary', type: 'button', text: '新增规则', onclick: function () { openForm(null); } })),
      h('div', { class: 'note', text: '修改在下一轮收集生效。' }),
      H.formBox, H.listBox,
      h('div', { class: 'card' }, h('h3', { text: '测试（按已保存的规则）' }),
        h('div', { class: 'row' }, urlInput, h('button', { class: 'btn', type: 'button', text: '测试', onclick: runPreview })),
        previewOut));
    H.built = true;
  }

  function loadRules() {
    if (!H.built) buildRules();
    api('GET', '/api/header-rules').then(function (rules) {
      var rows = rules.map(function (r) {
        return h('tr', null,
          h('td', { class: 'key mono', text: r.pattern }),
          h('td', { class: 'nw' }, badge(r.isExact ? '精确' : '通配', r.isExact ? 'info' : '')),
          h('td', { class: 'nw', text: r.headers.length }),
          h('td', { class: 'nw' }, h('label', null, h('input', { type: 'checkbox', checked: r.enabled, onchange: function (e) { toggleRule(r, e.target); } }), ' ', r.enabled ? '启用' : '停用')),
          h('td', { text: r.note || '' }),
          h('td', { class: 'nw', text: fmtTime(r.updatedAt) }),
          h('td', { class: 'nw' },
            h('button', { class: 'btn', type: 'button', text: '编辑', onclick: function () { openForm(r); } }), ' ',
            h('button', { class: 'btn danger', type: 'button', text: '删除', onclick: function () { deleteRule(r); } })));
      });
      setChildren(H.listBox, rules.length ? tableOf(['pattern', '类型', 'header 数', '启用', '备注', '更新时间', '操作'], rows) : empty('暂无规则'));
    }).catch(function (e) { setChildren(H.listBox, errBox(e)); });
  }

  function toggleRule(rule, box) {
    var want = box.checked;
    api('PUT', '/api/header-rules/' + rule.id, ruleBody(rule, want)).then(function () {
      toast(want ? '已启用（下一轮收集生效）' : '已停用（下一轮收集生效）');
      loadRules();
    }).catch(function (e) { fail(e); loadRules(); });
  }

  function deleteRule(rule) {
    confirmDialog('确定删除规则「' + rule.pattern + '」？', '删除').then(function (ok) {
      if (!ok) return;
      api('DELETE', '/api/header-rules/' + rule.id).then(function () { toast('已删除（下一轮收集生效）'); loadRules(); }).catch(fail);
    });
  }

  function openForm(rule) {
    var pattern = h('input', { type: 'text', value: rule ? rule.pattern : '', placeholder: '例如 * 或 res.example.com 或 *.example.com' });
    var note = h('input', { type: 'text', value: rule && rule.note ? rule.note : '' });
    var enabled = h('input', { type: 'checkbox', checked: rule ? rule.enabled : true });
    var pairs = h('div');
    var errOut = h('div');
    function addPair(name, value) {
      var n = h('input', { type: 'text', value: name || '', placeholder: '名称，如 Referer', 'data-role': 'name' });
      var v = h('input', { type: 'text', value: value || '', placeholder: '值（留空 = 删除该头）', 'data-role': 'value' });
      var row = h('div', { class: 'kvrow' }, n, v,
        h('button', { class: 'btn', type: 'button', text: '移除', onclick: function () { pairs.removeChild(row); } }));
      pairs.appendChild(row);
    }
    if (rule && rule.headers.length) rule.headers.forEach(function (x) { addPair(x.name, x.value); });
    else addPair('', '');
    function save() {
      var headers = [];
      Array.prototype.forEach.call(pairs.children, function (row) {
        var name = row.querySelector('[data-role=name]').value.trim();
        var value = row.querySelector('[data-role=value]').value;
        if (name || value) headers.push({ name: name, value: value });
      });
      var body = { pattern: pattern.value.trim(), headers: headers, enabled: enabled.checked, note: note.value.trim() };
      var req = rule ? api('PUT', '/api/header-rules/' + rule.id, body) : api('POST', '/api/header-rules', body);
      req.then(function () {
        toast('已保存（下一轮收集生效）');
        clear(H.formBox);
        loadRules();
      }).catch(function (e) { setChildren(errOut, errBox(e)); });
    }
    setChildren(H.formBox, h('div', { class: 'card form-grid' },
      h('h3', { text: rule ? '编辑规则 #' + rule.id : '新增规则' }),
      h('label', null, 'pattern', pattern),
      h('label', null, '备注', note),
      h('label', { class: 'row' }, enabled, h('span', { text: '启用' })),
      h('div', null, h('div', { class: 'small muted', style: 'margin-bottom:4px', text: '请求头（值留空表示删除该头）' }), pairs,
        h('button', { class: 'btn', type: 'button', text: '添加请求头', onclick: function () { addPair('', ''); } })),
      errOut,
      h('div', { class: 'row' },
        h('button', { class: 'btn primary', type: 'button', text: '保存', onclick: save }),
        h('button', { class: 'btn', type: 'button', text: '取消', onclick: function () { clear(H.formBox); } }))));
    H.formBox.scrollIntoView({ block: 'nearest' });
  }

  tabs.rules = { show: loadRules };

  // ---------- 启动 ----------
  P.root = document.getElementById('tab-progress');
  R.root = document.getElementById('tab-resources');
  Array.prototype.forEach.call(document.querySelectorAll('#tabs button'), function (b) {
    b.addEventListener('click', function () { showTab(b.getAttribute('data-tab')); });
  });
  buildProgress();
  pollStatus();
})();
