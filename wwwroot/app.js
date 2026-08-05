const state = { summary: null, accounts: [], rules: [] };

const esc = (value = "") => String(value).replace(/[&<>"']/g, ch => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
}[ch]));

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options,
  });
  if (!response.ok) throw new Error((await response.json()).error || "Request failed");
  return response.json();
}

function policyLabel(policy) {
  return {
    triage: "Triage only",
    draft: "Draft replies",
    outreach: "Outreach replies",
    monitor: "Monitor important",
    cleanup: "Warm-up cleanup",
  }[policy] || policy;
}

function categoryLabel(category) {
  return {
    reply: "Reply needed", bill: "Bill", uncertain: "Uncertain",
    genuine: "Important", warmup: "Warm-up", automated: "Automated",
    failure: "Failed", approval: "Needs approval",
  }[category] || category;
}

// Items arrive from several places now, not just mailboxes.
function sourceLabel(source) {
  return { email: "Email", baldrick: "Baldrick", percy: "Percy", n8n: "n8n" }[source] || source;
}

function renderToday() {
  const { counts, queue } = state.summary;
  document.querySelector("#today-count").textContent = counts.total_attention;
  document.querySelector("#stat-total").textContent = counts.total_attention;
  document.querySelector("#stat-drafts").textContent = counts.drafts;
  document.querySelector("#stat-bills").textContent = counts.bills;
  document.querySelector("#stat-uncertain").textContent = counts.uncertain;
  const container = document.querySelector("#queue");
  if (!queue.length) {
    container.innerHTML = `<div class="empty"><strong>You are finished.</strong><p>Nothing else needs you today.</p></div>`;
    return;
  }
  container.innerHTML = queue.map(item => `
    <article class="message" data-id="${item.id}">
      <div class="category ${esc(item.category)}">${esc(categoryLabel(item.category))}</div>
      <div class="message-main">
        <div class="message-top"><strong>${esc(item.sender)}</strong><span>${esc(sourceLabel(item.source))}</span></div>
        <h3>${esc(item.subject)}</h3>
        <p>${esc(item.reason)}</p>
      </div>
      <div class="message-action">
        ${item.needs_reply ? `<span class="draft-dot">Draft ready</span>` : ""}
        ${item.link ? `<a class="go-link" href="${esc(item.link)}" target="_blank" rel="noopener">Open →</a>` : ""}
        <button class="open-message">Review</button>
      </div>
    </article>
  `).join("");
  container.querySelectorAll(".message").forEach(card => {
    card.querySelector(".open-message").addEventListener("click", () => {
      const item = queue.find(x => x.id === Number(card.dataset.id));
      openMessage(item);
    });
  });
}

function renderAccounts() {
  document.querySelector("#accounts").innerHTML = state.accounts.map(account => `
    <article class="account-card">
      <div class="account-top"><span class="provider">${esc(account.provider)}</span><span class="status">${esc(account.connection_state)}</span></div>
      <h3>${esc(account.label)}</h3>
      <p>${esc(account.address)}</p>
      <dl>
        <div><dt>Policy</dt><dd>${esc(policyLabel(account.policy))}</dd></div>
        <div><dt>Warm-up detection</dt><dd>${account.detect_warmup ? "On" : "Off"}</dd></div>
        <div><dt>Reply drafts</dt><dd>${account.draft_replies ? "On" : "Off"}</dd></div>
        <div><dt>Quarantine</dt><dd>${account.cleanup_days} days</dd></div>
      </dl>
    </article>
  `).join("");
  const select = document.querySelector("#rule-account");
  select.innerHTML = `<option value="">All accounts</option>` + state.accounts.map(a =>
    `<option value="${a.id}">${esc(a.label)}</option>`
  ).join("");
}

function renderRules() {
  document.querySelector("#rules").innerHTML = state.rules.map(rule => `
    <article class="rule">
      <span class="scope">${esc(rule.account_label || "All accounts")}</span>
      <span>When <strong>${esc(rule.field)}</strong> ${esc(rule.operator)}</span>
      <code>${esc(rule.pattern)}</code>
      <span class="arrow">→</span>
      <span class="rule-action">${esc(categoryLabel(rule.action))}</span>
    </article>
  `).join("");
}

function renderHandled() {
  const handled = state.summary.handled;
  document.querySelector("#handled").innerHTML = handled.map(item => `
    <article><strong>${item.count}</strong><span>${esc(categoryLabel(item.category))}</span><small>kept out of your queue</small></article>
  `).join("");
}

function openMessage(item) {
  const dialog = document.querySelector("#message-dialog");
  document.querySelector("#dialog-content").innerHTML = `
    <p class="eyebrow">${esc(item.account_label)} · ${esc(policyLabel(item.account_policy))}</p>
    <h2>${esc(item.subject)}</h2>
    <p class="sender">${esc(item.sender)}</p>
    <div class="why"><strong>Why Percy showed this</strong><p>${esc(item.reason)}</p><small>${Math.round(item.confidence * 100)}% confidence</small></div>
    <div class="original"><strong>Message preview</strong><p>${esc(item.preview)}</p></div>
    ${item.draft_body ? `<label class="draft"><span>Proposed reply</span><textarea rows="8">${esc(item.draft_body)}</textarea></label><p class="never-send">This is a demonstration draft. Percy cannot send it.</p>` : ""}
    <div class="dialog-actions">
      ${item.draft_body ? `<button data-decision="approved" class="primary">Approve draft</button>` : `<button data-decision="done" class="primary">Handled</button>`}
      <button data-decision="snoozed">Tomorrow</button>
      <button data-decision="no_reply">No reply needed</button>
    </div>
  `;
  dialog.showModal();
  dialog.querySelectorAll("[data-decision]").forEach(button => {
    button.addEventListener("click", async () => {
      await api(`/api/messages/${item.id}/decision`, {
        method: "POST",
        body: JSON.stringify({ decision: button.dataset.decision }),
      });
      dialog.close();
      await refreshSummary();
    });
  });
}

async function refreshSummary() {
  state.summary = await api("/api/summary");
  renderToday();
  renderHandled();
}

async function init() {
  [state.summary, state.accounts, state.rules] = await Promise.all([
    api("/api/summary"), api("/api/accounts"), api("/api/rules")
  ]);
  renderToday();
  renderAccounts();
  renderRules();
  renderHandled();
}

document.querySelectorAll(".nav").forEach(button => {
  button.addEventListener("click", () => {
    document.querySelectorAll(".nav,.view").forEach(el => el.classList.remove("active"));
    button.classList.add("active");
    document.querySelector(`#view-${button.dataset.view}`).classList.add("active");
  });
});

document.querySelector("#rule-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const payload = Object.fromEntries(form.entries());
  payload.account_id = payload.account_id ? Number(payload.account_id) : null;
  await api("/api/rules", { method: "POST", body: JSON.stringify(payload) });
  state.rules = await api("/api/rules");
  renderRules();
  event.currentTarget.querySelector("[name=pattern]").value = "";
});

document.querySelector(".dialog-close").addEventListener("click", () =>
  document.querySelector("#message-dialog").close()
);

const updateClock = () => {
  document.querySelector("#clock").textContent = new Intl.DateTimeFormat("en-GB", {
    weekday: "long", day: "numeric", month: "long"
  }).format(new Date());
};
updateClock();
init().catch(error => {
  document.querySelector("main").innerHTML = `<div class="fatal"><strong>Percy Agent could not start.</strong><p>${esc(error.message)}</p></div>`;
});

