(() => {
    'use strict';
    const root = document.getElementById('wholly-embedded');
    const frame = document.getElementById('wholly-checkout-frame');
    if (!root || !frame) return;
    const status = document.getElementById('wholly-frame-status');
    const returnToInvoice = () => location.replace(root.dataset.returnUrl);
    // Only our same-origin return document may request navigation. No message,
    // frame event or browser redirect ever marks a payment as received/settled.
    window.addEventListener('message', event => {
        if (event.source === frame.contentWindow && event.origin === location.origin
            && event.data?.type === 'wholly:btcpay-return') returnToInvoice();
    });
    const loaded = () => { status.textContent = 'Pay once. Your payment is verified automatically.'; };
    frame.addEventListener('load', loaded);
    // A load event cannot prove that a cross-origin frame rendered successfully.
    // The full-page fallback stays visible regardless of frame status.
    const fallback = setTimeout(() => { status.textContent = 'If checkout does not appear, open the full checkout.'; }, 12000);
    let stopped = false, timer;
    async function check() {
        if (stopped) return;
        try {
            if (!document.hidden) {
                const response = await fetch(root.dataset.statusUrl, {credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(8000)});
                if (response.ok && (await response.json()).returnToInvoice === true) { returnToInvoice(); return; }
            }
        } catch { /* Keep checkout usable through network interruptions. */ }
        if (!stopped) timer = setTimeout(check, 10000);
    }
    timer = setTimeout(check, 10000);
    window.addEventListener('pagehide', () => { stopped = true; clearTimeout(timer); clearTimeout(fallback); });
    window.addEventListener('pageshow', event => { if (event.persisted) { stopped = false; check(); } });
})();
