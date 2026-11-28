using System.Security.Cryptography;
using System.Text;
using MessengerDesktop;

internal static class LocalHistoryChecks
{
    internal static void Run(string directory)
    {
        LocalHistoryStore.DirectoryPath = Path.Combine(directory, "history");
        LocalPinStore.DirectoryPath = Path.Combine(directory, "legacy-pins");
        const string oldAddress = "https://old.example.invalid/messenger";
        const string newAddress = "https://new.example.invalid/api/dev";
        const string impostor = "https://impostor.example.invalid/api/dev";
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fingerprint = ServerTrustStore.Accept(oldAddress, serverKey.ExportSubjectPublicKeyInfo(), null);
        ServerTrustStore.Accept(newAddress, serverKey.ExportSubjectPublicKeyInfo(), fingerprint);
        var differentFingerprint = ServerTrustStore.Accept(impostor, otherKey.ExportSubjectPublicKeyInfo(), null);
        var user = new string('1', 32);
        var otherUser = new string('2', 32);
        var chat = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        const string content = "private offline message 1d54b132";
        const string pinText = "private local pin 13131ea7";
        const string notStored = "excluded not-stored content 342b7c2";
        MessageDto Message(long seq, string? text = null) => new()
        {
            ChatId = chat, Id = chat + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), Sequence = seq, Text = text ?? "Message " + seq, SenderId = user
        };

        var m1 = Message(1, content);
        var m2 = Message(2);
        var m3 = Message(3);
        var m4 = Message(4);
        var m6 = Message(6);
        var m7 = Message(7);
        var m11 = Message(11);
        var oneShot = Message(12, notStored);
        oneShot.ContentState = "not_stored";
        m4.ReplyToId = m1.Id;
        m4.ForwardedFrom = new ForwardedFromDto { AuthorId = otherUser, AuthorName = "Forward author" };
        m4.Attachments = [new AttachmentDto { Id = 65, MessageId = m4.Id, FileType = "image/png" }];
        m4.Reactions = [new ReactionDto { Emoji = "👍", UserIds = [otherUser] }];
        LocalPinStore.Save(oldAddress, user, [new(chat, m1.Id, pinText), new("10", "100", "numeric pin")]);
        LocalPinStore.Save(impostor, user, [new(chat, m2.Id, "Wrong server pin")]);
        LocalPinStore.Save(oldAddress, otherUser, [new(chat, m3.Id, "Wrong account pin")]);

        var store = new LocalHistoryStore(fingerprint, user);
        store.ImportLegacyPins(user, ServerTrustStore.AddressesFor(fingerprint));
        Expect(store.ReadLocalPins().Single().MessageId == m1.Id, "Pin import crossed address trust, account or ID format");
        store.SaveChats([new ChatDto { Id = chat, Title = "Persistent chat", Type = "group" }]);
        store.SaveMembers(chat, [new MemberDto { Id = user, DisplayName = "Visible member" }]);
        store.SavePins(chat, [new PinDto { Message = m4, MessageId = m4.Id }]);
        store.SaveActivity(chat, [new GroupActivityItem(6, "joined", "Visible member", "2026-11-25T00:00:00Z", user)]);
        store.SavePage(chat, [m1, m2, m3, m4, m6, m7], 50, null, null);
        store.SavePage(chat, [m4, m7], 2, null, null);
        Expect(store.ReadMessages(chat, 50).Select(m => m.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 7L]),
            "Latest page did not remove a hidden row in its range or removed an older page");
        store.SavePage(chat, [m1], 2, 3, null);
        Expect(store.ReadMessages(chat, 50, before: 3).Select(m => m.Sequence).SequenceEqual([1L]),
            "Short older page did not remove an omitted row");
        store.PutMessage(chat, m11);
        store.SavePage(chat, [Message(8), Message(9)], 2, null, 7);
        Expect(store.ReadMessages(chat, 1).Single().Id == m11.Id, "Full forward page erased a newer row");
        store.PutMessage(chat, oneShot);
        store.UpdateMessage(chat, m4.Id, message => message.Text = "Persisted edit");

        // Open a new connection and cipher instance to exercise reads after restart.
        var reopened = new LocalHistoryStore(ServerTrustStore.Lookup(newAddress)!, user);
        Expect(reopened.ReadChats().Single().LastMessage?.Id == m11.Id, "Chat preview did not use newest saved sequence");
        Expect(reopened.ReadMessages(chat, 50).All(m => m.Id != oneShot.Id), "Unstored message entered the database");
        var previous = reopened.ReadMessages(chat, 50, before: 7);
        var restored = previous.Single(m => m.Id == m4.Id);
        Expect(previous[0].Text == content && restored.Text == "Persisted edit" && restored.ReplyToId == m1.Id &&
            restored.Attachments?.Single().Id == 65 && restored.ForwardedFrom?.AuthorId == otherUser &&
            restored.Reactions?.Single().UserIds?.Single() == otherUser, "History did not round-trip content and references");
        Expect(reopened.ReadLocalPins().Single().Text == pinText && reopened.ReadPins(chat).Single().MessageId == m4.Id &&
            reopened.ReadMembers(chat).Single().DisplayName == "Visible member" &&
            reopened.ReadActivity(chat, 30, null).Single().Id == 6, "Pins, members or activity did not persist");

        var otherAccount = new LocalHistoryStore(fingerprint, otherUser);
        var otherServer = new LocalHistoryStore(differentFingerprint, user);
        foreach (var isolated in new[] { otherAccount, otherServer })
            Expect(isolated.ReadChats().Count == 0 && isolated.ReadMessages(chat, 50).Count == 0 &&
                isolated.ReadLocalPins().Count == 0, "History crossed accounts or server identities");

        foreach (var path in Directory.EnumerateFiles(LocalHistoryStore.DirectoryPath))
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var secret in new[] { content, pinText, notStored, "Persisted edit" })
                Expect(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) == -1, "Cleartext was written to local storage");
        }

        reopened.UpdateLocalPins(pins => pins.RemoveAll(pin => pin.MessageId == m1.Id) > 0);
        reopened.ImportLegacyPins(user, ServerTrustStore.AddressesFor(fingerprint));
        Expect(reopened.ReadLocalPins().Count == 0, "Updating the address reimported a removed pin");
        Expect(LocalPinStore.Load(oldAddress, user).Count == 2, "Legacy pin source was modified");
        var previousPinDirectory = LocalPinStore.DirectoryPath;
        try
        {
            LocalPinStore.DirectoryPath = Path.Combine(previousPinDirectory, "migrated-numeric");
            LocalPinStore.Save(newAddress, user, [new(chat, m4.Id, "Converted numeric pin", otherUser, Sequence: 4)]);
        }
        finally { LocalPinStore.DirectoryPath = previousPinDirectory; }
        reopened.ImportLegacyPins(user, ServerTrustStore.AddressesFor(fingerprint));
        Expect(reopened.ReadLocalPins().Single().MessageId == m4.Id, "Later converted file was not imported");
        reopened.UpdateLocalPins(pins => pins.RemoveAll(pin => pin.MessageId == m4.Id) > 0);
        reopened.ImportLegacyPins(user, ServerTrustStore.AddressesFor(fingerprint));
        Expect(reopened.ReadLocalPins().Count == 0, "A removed numeric pin was imported again");
        reopened.SaveChats([]);
        Expect(reopened.ReadChats().Count == 0 && reopened.ReadMessages(chat, 50).Count == 0 &&
            reopened.ReadActivity(chat, 30, null).Length == 0 && reopened.ReadPins(chat).Count == 0,
            "Chat list reconciliation retained a removed chat");
        Console.WriteLine("PASS local history: encrypted storage, reopen, account/server isolation, page reconciliation, scoped legacy pin import and access removal");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
