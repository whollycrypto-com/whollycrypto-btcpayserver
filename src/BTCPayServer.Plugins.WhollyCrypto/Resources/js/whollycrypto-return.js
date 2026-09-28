(() => {
    'use strict';
    const link = document.getElementById('wholly-return-link');
    if (!link) return;
    if (window.parent !== window) window.parent.postMessage({type: 'wholly:btcpay-return'}, location.origin);
    else location.replace(link.href);
})();
