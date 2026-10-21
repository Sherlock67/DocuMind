// Small progressive enhancements. Every action is a plain HTML form post; this script only
// fills shared modal forms from the button that opened them and polls upload status.

(function () {
    "use strict";

    // A button like
    //   <button data-bs-toggle="modal" data-bs-target="#renameModal"
    //           data-action="/documents/123/rename" data-title="Rename" data-field-file-name="a.pdf" data-exclude="id1,id2">
    // points the modal's form at data-action, sets the title, pre-fills inputs named after
    // data-field-* (file-name -> fileName) and disables <option>s listed in data-exclude.
    document.addEventListener("show.bs.modal", function (e) {
        const trigger = e.relatedTarget;
        if (!trigger) return;

        const modal = e.target;
        const form = modal.querySelector("form");
        if (!form) return;

        if (trigger.dataset.action) form.action = trigger.dataset.action;
        if (trigger.dataset.title) {
            const title = modal.querySelector(".modal-title");
            if (title) title.textContent = trigger.dataset.title;
        }

        for (const [key, value] of Object.entries(trigger.dataset)) {
            if (!key.startsWith("field")) continue;
            const name = key.charAt(5).toLowerCase() + key.slice(6);
            const input = form.querySelector(`[name="${name}"]`);
            if (!input) continue;
            if (input.type === "checkbox") input.checked = value === "true";
            else input.value = value;
        }

        const excluded = (trigger.dataset.exclude || "").split(",").filter(Boolean);
        form.querySelectorAll("select option").forEach(o => { o.disabled = excluded.includes(o.value); });
    });

    document.addEventListener("shown.bs.modal", function (e) {
        const first = e.target.querySelector("input:not([type=hidden]):not([disabled]), select, textarea");
        if (first) first.focus();
    });

    // Enable Bootstrap tooltips (failure reasons on status badges).
    document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(el => new bootstrap.Tooltip(el));

    // Upload results: poll /documents/status until every document is Completed or Failed.
    const board = document.querySelector("[data-status-poll]");
    if (board) {
        const badgeClass = { Completed: "text-bg-success", Failed: "text-bg-danger", Processing: "text-bg-info", Queued: "text-bg-secondary" };
        const pending = () => [...board.querySelectorAll("[data-document-id]")]
            .filter(el => el.dataset.status === "Queued" || el.dataset.status === "Processing");

        async function poll() {
            const waiting = pending();
            if (waiting.length === 0) { board.querySelector("[data-poll-spinner]")?.remove(); return; }

            const qs = waiting.map(el => "ids=" + encodeURIComponent(el.dataset.documentId)).join("&");
            try {
                const res = await fetch("/documents/status?" + qs, { headers: { Accept: "application/json" } });
                if (res.ok) {
                    for (const s of await res.json()) {
                        const el = board.querySelector(`[data-document-id="${s.id}"]`);
                        if (!el) continue;
                        el.dataset.status = s.status;
                        el.className = "badge " + (badgeClass[s.status] || "text-bg-secondary");
                        el.textContent = s.status;
                        el.title = s.failureReason || "";
                    }
                }
            } catch { /* transient; try again */ }
            setTimeout(poll, 2000);
        }
        setTimeout(poll, 1500);
    }
})();
