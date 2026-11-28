using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    private readonly LocalHistoryStore? _history;
    private Task _historyWrites = Task.CompletedTask;
    private readonly Dictionary<string, long> _historyVersions = new();
    private long _chatListVersion;

    private LocalHistoryStore? OpenLocalHistory(string? legacyServerAddress)
    {
        if (_api.ServerFingerprint is not { } fingerprint) return null;
        var store = TryLocalHistory(() => new LocalHistoryStore(fingerprint, _me.Id));
        if (store != null)
            TryLocalHistory(() =>
            {
                var addresses = ServerTrustStore.AddressesFor(fingerprint);
                if (legacyServerAddress != null &&
                    TryLocalHistory(() => MessengerClient.NormalizeServerAddress(legacyServerAddress)) is { } normalized &&
                    addresses.Contains(normalized, StringComparer.Ordinal))
                    addresses = [.. addresses, legacyServerAddress.Trim().TrimEnd('/')];
                store.ImportLegacyPins(_me.Id, addresses);
            });
        return store;
    }

    private static bool LocalStorageError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SqliteException or CryptographicException
            or JsonException or FormatException or ServerIdentityException;

    private static T? TryLocalHistory<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) when (LocalStorageError(ex))
        {
            Debug.WriteLine("Local history unavailable: " + ex.GetType().Name);
            return default;
        }
    }

    private static void TryLocalHistory(Action action) =>
        TryLocalHistory(() => { action(); return true; });

    private void CacheHistory(Action<LocalHistoryStore> save)
    {
        if (_history is not { } store || _closed) return;
        var previous = _historyWrites;
        _historyWrites = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            TryLocalHistory(() => save(store));
        });
    }

    private async Task<T?> ReadLocalHistory<T>(Func<LocalHistoryStore, T> read) where T : class
    {
        if (_history is not { } store || _closed) return null;
        await _historyWrites;
        return await Task.Run(() => TryLocalHistory(() => read(store)));
    }

    private static bool CanUseLocalHistory(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or ApiException { Status: >= 500 and <= 599 };

    private long HistoryVersion(string chatId) => _historyVersions.GetValueOrDefault(chatId);
    private void InvalidateHistoryPage(string chatId) => _historyVersions[chatId] = HistoryVersion(chatId) + 1;

    private void RemoveCachedChat(string chatId, bool purgePins = false)
    {
        _chatListVersion++;
        InvalidateHistoryPage(chatId);
        CacheHistory(store =>
        {
            store.RemoveChat(chatId);
            if (purgePins) store.UpdateLocalPins(pins => pins.RemoveAll(pin => pin.ChatId == chatId) > 0);
        });
    }

    private async Task<List<MessageDto>> FetchHistory(string chatId, int limit, long? beforeSequence = null,
        long? afterSequence = null, bool preferCacheOnError = true)
    {
        var version = HistoryVersion(chatId);
        var listVersion = _chatListVersion;
        try
        {
            var page = await _api.GetMessages(chatId, limit, beforeSequence: beforeSequence, afterSequence: afterSequence);
            if (version == HistoryVersion(chatId) && listVersion == _chatListVersion)
                CacheHistory(store => store.SavePage(chatId, page, limit, beforeSequence, afterSequence));
            return page;
        }
        catch (ApiException ex) when (ex.Status is 403 or 404)
        {
            DropChat(chatId);
            throw;
        }
        catch (Exception ex) when (preferCacheOnError && afterSequence == null && CanUseLocalHistory(ex))
        {
            var cached = await ReadLocalHistory(store => store.ReadMessages(chatId, limit, beforeSequence));
            if (cached == null) throw;
            if (SelectedChat?.Id == chatId) Error = "Нет соединения. Показана история с устройства.";
            return cached;
        }
    }

    private void CacheMessageRemoval(string chatId, string id, bool everyone = false)
    {
        InvalidateHistoryPage(chatId);
        CacheHistory(store =>
        {
            store.UpdateSharedPin(chatId, id, null);
            if (!everyone) store.RemoveMessage(chatId, id);
            else store.UpdateMessage(chatId, id, message =>
            {
                message.ContentState = "deleted";
                message.Text = null;
                message.Reactions = null;
                message.Attachments = null;
                message.ForwardedFrom = null;
            });
        });
    }

    private void CacheEvent(WsEventDto e)
    {
        if (e.ChatId is not { } chatId) return;
        switch (e.Type)
        {
            case "chat_created":
                _chatListVersion++;
                break;
            case "message_deleted" when e.ForEveryone == false && e.MessageId is { } hidden:
                CacheMessageRemoval(chatId, hidden);
                break;
            case "message":
            case "message_edited":
            case "message_deleted":
                if (e.Message is { } message && Chats.Any(chat => chat.Id == chatId))
                {
                    InvalidateHistoryPage(chatId);
                    CacheHistory(store =>
                    {
                        store.PutMessage(chatId, message);
                        if (e.Type == "message_deleted") store.UpdateSharedPin(chatId, message.Id, null);
                        else if (e.Type == "message_edited")
                            store.UpdateSharedPin(chatId, message.Id, message, onlyIfPresent: true);
                    });
                }
                break;
            case "attachment_added" when e.Attachment is { } attachment && e.MessageId == attachment.MessageId:
                InvalidateHistoryPage(chatId);
                CacheHistory(store => store.UpdateMessage(chatId, attachment.MessageId, message =>
                {
                    message.Attachments ??= [];
                    if (!message.Attachments.Any(a => a.Id == attachment.Id)) message.Attachments.Add(attachment);
                }));
                break;
            case "reaction_added":
            case "reaction_removed":
                if (e.MessageId is { } id && e.UserId is { } user && e.Emoji is { } emoji)
                {
                    InvalidateHistoryPage(chatId);
                    CacheHistory(store => store.UpdateMessage(chatId, id, message =>
                    {
                        message.Reactions ??= [];
                        var reactions = message.Reactions.FirstOrDefault(r => r.Emoji == emoji);
                        if (e.Type == "reaction_removed") reactions?.UserIds?.RemoveAll(value => value == user);
                        else
                        {
                            if (reactions == null)
                            {
                                reactions = new ReactionDto { Emoji = emoji };
                                message.Reactions.Add(reactions);
                            }
                            reactions.UserIds ??= [];
                            if (!reactions.UserIds.Contains(user)) reactions.UserIds.Add(user);
                        }
                    }));
                }
                break;
            case "message_pinned" when e.Message is { } pinned:
                CacheSharedPin(chatId, pinned.Id, pinned);
                break;
            case "message_unpinned" when e.MessageId is { } unpinned:
                CacheSharedPin(chatId, unpinned, null);
                break;
            case "member_added":
            case "member_removed":
            case "member_left":
            case "member_role_changed":
            case "owner_changed":
                InvalidateHistoryPage(chatId);
                if (e.UserId != _me.Id || e.Type is not ("member_left" or "member_removed"))
                    CacheHistory(store => store.ClearMembers(chatId));
                break;
        }
    }

    private void CacheSharedPin(string chatId, string id, MessageDto? message)
    {
        InvalidateHistoryPage(chatId);
        CacheHistory(store => store.UpdateSharedPin(chatId, id, message));
    }

    private async Task<List<MemberDto>> FetchHistoryMembers(string chatId)
    {
        var version = HistoryVersion(chatId);
        try
        {
            var members = await _api.GetMembers(chatId);
            if (version == HistoryVersion(chatId))
                CacheHistory(store => store.SaveMembers(chatId, members));
            return members;
        }
        catch (ApiException ex) when (ex.Status is 403 or 404)
        {
            DropChat(chatId);
            throw;
        }
        catch (Exception ex) when (CanUseLocalHistory(ex))
        {
            var cached = await ReadLocalHistory(store => store.ReadMembers(chatId));
            if (cached == null) throw;
            return cached;
        }
    }
}
