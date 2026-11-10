import { useState, type FormEvent } from 'react';
import type { ChatModelEdit } from '@shared/ipc';
import { PROVIDERS, providerName, type Provider } from '@shared/providers';

export function ChatModelDialog({ request, onClose }: { request: ChatModelEdit; onClose: () => void }) {
  const [provider, setProvider] = useState<Provider>(request.provider);
  const [model, setModel] = useState(request.model || '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError('');
    try {
      await window.reframe.changeChatModels(request.items, provider, model.trim());
      onClose();
    } catch (failure) {
      setError(String(failure));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="modal-backdrop" onMouseDown={(event) => !busy && event.target === event.currentTarget && onClose()}>
      <form
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="chat-model-title"
        onSubmit={submit}
        onKeyDown={(event) => event.key === 'Escape' && !busy && onClose()}
      >
        <h2 id="chat-model-title">Change selected chats’ model</h2>
        <p>{request.items.length} selected chats. Only inactive native chats can change.</p>
        <label className="field">
          <span>Model provider</span>
          <select
            value={provider}
            disabled={busy}
            onChange={(event) => {
              setProvider(event.target.value as Provider);
              setModel('');
            }}
          >
            {PROVIDERS.map((value) => (
              <option key={value} value={value}>
                {providerName(value)}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Model</span>
          <input
            autoFocus
            value={model}
            maxLength={300}
            disabled={busy}
            placeholder="Provider default"
            spellCheck={false}
            onChange={(event) => setModel(event.target.value)}
          />
        </label>
        <p className="provider-note">
          Applies {providerName(provider)} and this exact model ID to the selected eligible chats, including chats
          currently using another provider. Leave Model empty to use that provider’s default. Existing conversations are
          preserved.
        </p>
        {error && (
          <p className="form-error" role="alert">
            {error}
          </p>
        )}
        <div className="button-row end">
          <button type="button" className="button" disabled={busy} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="button primary" disabled={busy}>
            {busy ? 'Applying…' : 'Apply to selected chats'}
          </button>
        </div>
      </form>
    </div>
  );
}
