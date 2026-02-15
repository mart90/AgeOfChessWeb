export function navigate(url) {
  history.pushState({}, '', url);
  window.dispatchEvent(new PopStateEvent('popstate'));
}
