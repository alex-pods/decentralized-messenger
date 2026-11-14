namespace Messenger.Api.Realtime;

public static class WsProtocol
{
    public static object Describe(string pathBase) => new
    {
        url = $"ws://<host>{pathBase}/ws?user_session_token=<token>",

        auth = "Session token: user_session_token query parameter " +
               "(token alias supported). HTTP uses Bearer. " +
               "Logout closes only that session's sockets.",

        close_codes = new
        {
            c1000 = "normal closure",
            c1001 = "server shutting down",
            c1009 = "text messages only, up to 16 KiB",
            c4001 = "session revoked (logout)",
            c4002 = "session expired or revoked",
            c4008 = "slow consumer: 256-event queue overflow; " +
                    "reconnect and synchronize"
        },

        actions = new object[]
        {
            new
            {
                action = "ping",
                reply = "pong"
            },
            new
            {
                action = "follow",
                args = "chat_id",
                reply = "followed | error",
                note = "Session and membership required; " +
                       "repeated follow is harmless."
            },
            new
            {
                action = "unknown / malformed JSON",
                reply = "error{detail}"
            }
        },

        events = new object[]
        {
            new { type = "connected", follow = false, payload = "user_id" },
            new { type = "pong", follow = false },
            new { type = "followed", follow = false, payload = "chat_id" },

            new
            {
                type = "chat_created",
                follow = false,
                payload = "chat_id, chat"
            },
            new
            {
                type = "chat_hidden",
                follow = false,
                payload = "chat_id",
                to = "only the user who hid it"
            },
            new
            {
                type = "chat_deleted",
                follow = false,
                payload = "chat_id",
                to = "last member of a closed group"
            },

            new
            {
                type = "message",
                follow = true,
                payload = "chat_id, message"
            },
            new
            {
                type = "message_edited",
                follow = true,
                payload = "chat_id, message"
            },
            new
            {
                type = "message_deleted",
                follow = true,
                payload = "chat_id, message_id, message, for_everyone",
                note = "for_everyone=false goes only to that user's " +
                       "sessions, without follow. " +
                       "Deleting for everyone also drops a pin and sends message_unpinned"
            },
            new
            {
                type = "message_pinned",
                follow = true,
                payload = "chat_id, message_id, user_id, message"
            },
            new
            {
                type = "message_unpinned",
                follow = true,
                payload = "chat_id, message_id, user_id, message"
            },

            new
            {
                type = "reaction_added",
                follow = true,
                payload = "chat_id, message_id, user_id, emoji"
            },
            new
            {
                type = "reaction_removed",
                follow = true,
                payload = "chat_id, message_id, user_id, emoji"
            },
            new
            {
                type = "attachment_added",
                follow = true,
                payload = "chat_id, message_id, attachment"
            },

            new
            {
                type = "member_added",
                follow = true,
                payload = "chat_id, user_id, user"
            },
            new
            {
                type = "member_removed",
                follow = true,
                payload = "chat_id, user_id",
                to = "current members and removed user"
            },
            new
            {
                type = "member_left",
                follow = true,
                payload = "chat_id, user_id",
                to = "current members and departed user"
            },
            new
            {
                type = "member_role_changed",
                follow = false,
                payload = "chat_id, user_id, role"
            },
            new
            {
                type = "owner_changed",
                follow = false,
                payload = "chat_id, user_id"
            },

            new
            {
                type = "read",
                follow = true,
                payload = "chat_id, user_id, last_read_message_id"
            },
            new
            {
                type = "avatar_changed",
                follow = false,
                payload = "user_id",
                to = "self and users sharing any chat"
            }
        },

        reconnect = "Reconnect, follow chats, then GET " +
                    "/chats/{id}/messages?after_id=<last id> until empty. " +
                    "Deduplicate by message id. Refresh chat/member lists " +
                    "and loaded history to recover edits, deletions and " +
                    "reactions: after_id only recovers new messages.",

        delivery = "delivered_at means queued to at least one other user's " +
                   "online connection, not a client acknowledgement. " +
                   "Read receipts require POST /chats/{id}/read.",

        scale = "Delivery within one server process. " +
                "No durable event replay or multi-instance routing."
    };
}