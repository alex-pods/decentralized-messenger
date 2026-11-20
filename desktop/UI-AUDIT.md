# Desktop UI redesign audit

## Goal
A quiet, readable messenger with a consistent interaction model. A completed
screen must work with real data, light and dark themes, keyboard input, and
short or narrow windows.

## Findings and acceptance criteria
| Screen | Finding | Required result |
|---|---|---|
| Shell | Rail repeats chats and group actions | Two columns, one new-conversation menu, one account entry |
| Chat list | Filter replaces collection and loses selection | Filter retains active conversation and updates with incoming data |
| Conversation | Action strip competes with composer | Contextual message menu, no persistent action strip |
| Message | Tiny unlabeled action icons, duplicated templates | Named contextual actions, same permissions and commands |
| Composer | Weak visual hierarchy and unclear shortcut | Single send action, visible reply/edit context and cancellation |
| Login | Register and sign-in share ambiguous form | Explicit mode, busy and connection/error states |
| Profile | Avatar and settings compete with save | Dedicated scrollable form, save at bottom |
| New chat/group | Shared form makes sequence unclear | Separate title/details, people selection, final action |
| Members | Management buttons under every person | Compact member rows with contextual management |
| Forward | Plain list lacks context | Dedicated destination picker |
| Appearance | Raw HEX form dominates | Preview cards, color picker, optional advanced customization |
| Dialogs | Fixed sizes exceed narrow windows | Bounded width/height and scrolling; Escape closes |
| Theme | Only selected screens verified | Full screen matrix in light/dark, persistence and cancel checks |

## Validation
Use disposable local backend and accounts. Capture each screen at desktop and
narrow sizes. Exercise normal client self-check, filtering, context actions,
theme restore/cancel, and error states. Keep backend and API behavior intact.

## Visual direction after review
Telegram Desktop is a reference for density and interaction, not a visual template.
The application uses neutral graphite surfaces, a muted blue accent, a slim chat
selection marker, a persistent account entry, and its own compact forms. Avoid
floating cards, oversized bubbles, redundant avatars in direct chats, and action
strips. Lucide supplies the interface icons; its license ships with the client.

## Verified on Windows
- Release build: zero warnings and errors.
- Disposable local backend: desktop integration self-check and pin-strip check.
- Screen captures: login/registration/errors, empty conversation, direct/group
  chats, profile, members, forwarding, reply, appearance and custom colors.
- Light/dark screen matrix and four preset conversation previews.
- 360/520 px windows, populated group selection and long participant names.
- Search retains the active conversation; empty results have a visible state.
- Own-message edit action is present; another person's message cannot be edited.
- Theme selection marker, persistence roundtrip, live preset preview and cancel.
- Existing native window chrome retained to avoid overlapping macOS controls.

macOS/Linux rendering and public-server integration were not tested in this run.
Screenshots use disposable demonstration accounts, not production conversations.
- Closing appearance cancels queued color/name events as well as synchronous
  changes, so a dismissed preview cannot switch the application theme later.
