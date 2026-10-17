const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('hub', Object.freeze({
  invoke: async (command, payload = {}) => {
    const result = await ipcRenderer.invoke('hub:command', command, payload);
    if (!result.ok) {
      const error = new Error(result.error); error.code = result.error; error.mutationOutcome = result.mutationOutcome; throw error;
    }
    return result.value;
  },
  onEvent: callback => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, data) => callback(data);
    ipcRenderer.on('hub:event', listener);
    return () => ipcRenderer.removeListener('hub:event', listener);
  }
}));
