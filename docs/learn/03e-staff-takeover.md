# LEARN 3e: a person can take over (and the store desk)

## Chats

The concierge chat is kept in the Assistant's own database (`assistantdb`, schema `assistant`), so store staff can see it and step in.

| Who | What |
|---|---|
| **Customer** | Chats as before. The reply says who is answering: `answering: concierge`, or `staff` with the person's name. The page polls `GET /api/assistant/conversations/{id}/messages?after=` and shows staff replies under the person's name, plus "joined" and "handed back" notes. |
| **Staff** (`support.chat`) | `GET /api/assistant/conversations` lists the store's chats from the last two days. **Take over** pauses the concierge: the customer's messages go to the person, and the model isn't called at all. **Reply** posts under your name. **Hand back** returns the chat to the concierge. |

**Rules:**
- Staff see only their own store's chats, and a customer sees only their own.
- One person holds a chat at a time (a conditional switch; anyone else gets `taken`).
- An agent's call can never act as staff (`X-BuilderPro-Agent` is refused).

**Dev users:** bob and mia have `support.chat`.

## Store desk (`/desk`, avatar menu → Store desk)

| Tab | Who | What |
|---|---|---|
| **Chats** | `support.chat` | Conversation list, the thread, then Take over, Reply and Hand back |
| **Work** | `scheduling.write` | `GET /api/staff/queue`:<br>• booked visits to measure (**Measured as quoted** records the quoted sizes)<br>• installations to schedule or move on (date, then being made, installing, done)<br>• scheduling calls where the customer asked for a person, or no time fitted their hours |

Every action calls the API's own endpoint (6b `visit/measured`, 6c `install`), with that endpoint's rules.

## Not yet

- ~~A form for different measured sizes~~: done (Enter measured sizes).
- Notifying staff when a chat needs them; for now they poll every 5 s.
- Transcripts given to the model after a hand-back; it sees the page's own history.
