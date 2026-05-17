# ez-9router

Chrome-compatible MV3 extension for sending selected text or right-clicked images to a local 9router model.

## Features

- Right-click selected text or an image and run a prompt.
- Right-click a page to use snip mode, prompt-guided snip mode, custom snip prompts, or send the full HTML.
- Popup dashboard for 9router URL, API key, default model, and prompt labels.
- Per-site conversation memory can be resumed or cleared from the popup.
- Debug logs capture sanitized request shape, image metadata, timing, and provider errors for troubleshooting.
- Fetches available models from `GET /v1/models`.
- Opens a Grammarly-style inline answer card with formatted answers and copy support.
- Supports light, dark, and system themes using a 9router-style orange-red accent.

## Local Setup

1. Open `chrome://extensions`.
2. Enable Developer mode.
3. Click **Load unpacked**.
4. Select this repository folder.
5. Open the extension popup and confirm:
   - URL: `http://127.0.0.1:20128`
   - API key: `sk_9-router`
   - Model: `cx/gpt-5.5` or another model from 9router.

## Notes

- No provider secrets are committed. Settings are stored in local browser extension storage.
- Image prompts require a model/provider that accepts OpenAI-style vision content.
- Some routed models may reject image payloads even when text works. Use the popup debug logs to inspect the provider error.
- 9router must allow requests from the extension context and be reachable from the browser.
