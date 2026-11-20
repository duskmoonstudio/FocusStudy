namespace FocusStudy
{
    internal static class WebScripts
    {
        /// <summary>
        /// Injected into every page. Replaces the native Windows file dialog (which could browse the whole PC)
        /// with the app's own picker that only lists files from the study folder.
        /// </summary>
        public const string FilePicker = @"
(() => {
  if (window.__focusPicker || !window.chrome || !window.chrome.webview) return;
  window.__focusPicker = true;
  let pending = null;
  function request(input) {
    pending = input;
    window.chrome.webview.postMessage(JSON.stringify({ type: 'pickFiles', multiple: !!input.multiple, accept: input.accept || '' }));
  }
  const origClick = HTMLInputElement.prototype.click;
  HTMLInputElement.prototype.click = function () {
    if (this.type === 'file') { request(this); return; }
    return origClick.call(this);
  };
  if (HTMLInputElement.prototype.showPicker) {
    const origShow = HTMLInputElement.prototype.showPicker;
    HTMLInputElement.prototype.showPicker = function () {
      if (this.type === 'file') { request(this); return; }
      return origShow.call(this);
    };
  }
  document.addEventListener('click', e => {
    const path = e.composedPath ? e.composedPath() : [e.target];
    for (const el of path) {
      if (el instanceof HTMLInputElement && el.type === 'file') { e.preventDefault(); e.stopPropagation(); request(el); return; }
      if (el instanceof HTMLLabelElement && el.control && el.control.type === 'file') { e.preventDefault(); e.stopPropagation(); request(el.control); return; }
    }
  }, true);
  for (const n of ['showOpenFilePicker', 'showSaveFilePicker', 'showDirectoryPicker']) {
    if (window[n]) window[n] = () => Promise.reject(new DOMException('File dialogs are disabled in focus mode', 'AbortError'));
  }
  window.chrome.webview.addEventListener('message', ev => {
    let d = ev.data;
    if (typeof d === 'string') { try { d = JSON.parse(d); } catch { return; } }
    if (!d || d.type !== 'files' || !pending) return;
    const input = pending; pending = null;
    if (!d.files || d.files.length === 0) return;
    const dt = new DataTransfer();
    for (const f of d.files) {
      const bin = atob(f.data);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      dt.items.add(new File([bytes], f.name, { type: f.mime }));
    }
    input.files = dt.files;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  });
})();";
    }
}
