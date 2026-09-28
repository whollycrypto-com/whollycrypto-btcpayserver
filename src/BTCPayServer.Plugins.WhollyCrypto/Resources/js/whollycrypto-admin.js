(() => {
  const limit = document.getElementById('LimitMethods');
  const picker = document.getElementById('wholly-method-picker');
  if (!limit || !picker) return;
  const update = () => { picker.hidden = !limit.checked; };
  limit.addEventListener('change', update);
  update();
  const search = document.getElementById('wholly-method-search');
  search?.addEventListener('input', () => {
    const text = search.value.trim().toLowerCase();
    picker.querySelectorAll('[data-method-search]').forEach(row => {
      row.hidden = !row.dataset.methodSearch.toLowerCase().includes(text);
    });
  });
})();
