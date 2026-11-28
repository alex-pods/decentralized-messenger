using Messenger.Api.Common.Errors;

namespace Messenger.Api.Antispam;

public sealed record ChallengeState(string Phase, double CountdownMs, double RemainingMs, int Score, IReadOnlyList<AimPoint> Targets, int AckSeq = 0, bool Hit = false, string? Reason = null, bool Shot = false, int Hits = 0, int Misses = 0, double Accuracy = 0);
public sealed record ChallengeInput(string Type, int Seq, double X = 0, double Y = 0);

public sealed class AntispamService(AntispamOptions options, AntispamRepository repository, TimeProvider clock)
{
    readonly object _gate = new();
    readonly Dictionary<string, Attempt> _attempts = [];
    public bool Enabled => options.Enabled;
    public bool Required(string userId) => Enabled && repository.BlockId(userId) != null;
    public void RequireUnblocked(string userId)
    {
        if (Required(userId)) throw new AppException(429, "antispam_required", "Подтвердите, что вы человек");
    }

    public void CheckSend(string userId, string chatId, string? clientRef)
    {
        if (!Enabled) return;
        var key = clientRef == null ? "new:" + Guid.NewGuid().ToString("N") : $"send:{chatId}:{clientRef}";
        if (!repository.Reserve(userId, key, clientRef, clock.GetUtcNow().ToUnixTimeMilliseconds(), options))
            throw new AppException(429, "antispam_required", "Подтвердите, что вы человек");
    }

    public string Create(string userId)
    {
        var blockId = Enabled ? repository.BlockId(userId) : null;
        if (blockId == null) throw AppException.Conflict("antispam_not_required", "Проверка не требуется");
        lock (_gate)
        {
            foreach (var key in _attempts.Where(p => Elapsed(p.Value.Created) > 90000).Select(p => p.Key).ToArray()) _attempts.Remove(key);
            if (_attempts.TryGetValue(userId, out var existing) && !existing.Terminal && Elapsed(existing.Created) < 45000)
                throw AppException.Conflict("antispam_in_progress", "Проверка уже открыта на одном из устройств");
            if (_attempts.Count >= 1024 && !_attempts.ContainsKey(userId)) throw new AppException(503, "antispam_busy", "Повторите проверку позже");
            var attempt = new Attempt { Id = Guid.NewGuid().ToString("N"), BlockId = blockId, Created = clock.GetTimestamp() };
            _attempts[userId] = attempt;
            return attempt.Id;
        }
    }

    public ChallengeState Start(string userId, string id)
    {
        lock (_gate)
        {
            var a = Get(userId, id);
            if (a.Started != null || a.Terminal || Elapsed(a.Created) > 10000)
                throw AppException.Conflict("antispam_expired", "Начните проверку заново");
            a.Started = clock.GetTimestamp();
            return State(a);
        }
    }

    public ChallengeState Input(string userId, string id, ChallengeInput input)
    {
        lock (_gate)
        {
            var a = Get(userId, id);
            if (a.Terminal) return State(a);
            if (a.Started == null || input.Seq != a.Sequence + 1 || input.Seq > 3000)
                return Fail(a, "invalid_sequence");
            a.Sequence = input.Seq;
            if (input.Type == "fail") return Fail(a, "interrupted");
            var elapsed = Elapsed(a.Started.Value);
            if (a.QualifiedAt is { } qualified)
            {
                if (Elapsed(qualified) > 2000) return Fail(a, "confirmation_timeout");
                if (input.Type == "finish")
                {
                    if (!repository.Clear(userId, a.BlockId)) return Fail(a, "superseded");
                    a.Phase = "passed";
                    return State(a);
                }
            }
            else if (elapsed >= PrecisionRound.CountdownMs + PrecisionRound.DurationMs)
            {
                if (a.Game.Score < PrecisionRound.RequiredScore) return Fail(a, "time_up");
                a.Phase = "qualified"; a.QualifiedAt = clock.GetTimestamp();
                return State(a);
            }
            if (input.Type == "heartbeat") return State(a);
            if (input.Type != "shot" || a.QualifiedAt != null || elapsed < PrecisionRound.CountdownMs ||
                !double.IsFinite(input.X) || !double.IsFinite(input.Y) || Math.Abs(input.X) > 100000 || Math.Abs(input.Y) > 100000)
                return Fail(a, "invalid_shot");
            if (elapsed - a.LastShot < 50) return Fail(a, "shot_rate");
            a.LastShot = elapsed;
            var hit = a.Game.Click(input.X, input.Y);
            return State(a) with { Hit = hit, Shot = true };
        }
    }

    public void Abort(string userId, string id)
    {
        lock (_gate)
            if (_attempts.TryGetValue(userId, out var a) && a.Id == id && !a.Terminal) Fail(a, "interrupted");
    }

    double Elapsed(long timestamp) => clock.GetElapsedTime(timestamp).TotalMilliseconds;
    Attempt Get(string userId, string id) => _attempts.TryGetValue(userId, out var a) && a.Id == id
        ? a : throw AppException.NotFound("Проверка не найдена");
    ChallengeState Fail(Attempt a, string reason) { a.Phase = "failed"; a.Reason = reason; return State(a); }
    ChallengeState State(Attempt a)
    {
        var elapsed = a.Started is { } start ? Elapsed(start) : 0;
        var countdown = Math.Max(0, PrecisionRound.CountdownMs - elapsed);
        return new(a.Phase == "running" && countdown > 0 ? "countdown" : a.Phase, countdown,
            Math.Clamp(PrecisionRound.DurationMs + PrecisionRound.CountdownMs - elapsed, 0, PrecisionRound.DurationMs),
            a.Game.Score, countdown > 0 || a.Terminal || a.QualifiedAt != null ? [] : a.Game.Targets.ToArray(), a.Sequence, Reason: a.Reason,
            Hits: a.Game.Hits, Misses: a.Game.Misses, Accuracy: a.Game.Hits + a.Game.Misses == 0 ? 0 : 100d * a.Game.Hits / (a.Game.Hits + a.Game.Misses));
    }

    sealed class Attempt
    {
        public required string Id;
        public required string BlockId;
        public long Created;
        public long? Started;
        public long? QualifiedAt;
        public PrecisionRound Game { get; } = new();
        public string Phase = "running";
        public string? Reason;
        public int Sequence;
        public double LastShot = -1000;
        public bool Terminal => Phase is "passed" or "failed";
    }
}
