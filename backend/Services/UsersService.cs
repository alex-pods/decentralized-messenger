using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

public interface IUsersService
{
    Task<ProfileResponse> GetProfile(long userId);
    Task<ProfileResponse> GetProfileById(long userId);
    Task<List<UserCardResponse>> Search(long viewerId, string query, bool prefix);
    Task UpdateProfile(long userId, UpdateProfileRequest request);
    Task UpdateProfileWithAvatar(long userId, UpdateProfileRequest request, IFormFile file, CancellationToken cancellation);
}

public sealed class UsersService(IUsersRepository users, IAvatarsRepository avatars, IAvatarsService avatarStorage) : IUsersService
{
    public Task<ProfileResponse> GetProfile(long userId) => GetProfileById(userId);

    public async Task<ProfileResponse> GetProfileById(long userId)
    {
        var user = await users.GetById(userId) ?? throw AppException.NotFound("user not found");
        var avatar = (await avatars.GetByUserIds([userId])).GetValueOrDefault(userId);
        return ProfileResponse.From(user, avatar?.Path);
    }

    public async Task<List<UserCardResponse>> Search(long viewerId, string query, bool prefix)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0) throw AppException.BadRequest("query is required");
        if (prefix && normalized.Length is < 3 or > 16) return [];
        return (await users.Search(viewerId, normalized, 20, prefix)).Select(UserCardResponse.From).ToList();
    }

    public Task UpdateProfile(long userId, UpdateProfileRequest request)
    {
        var name = ValidateProfile(request);
        return users.UpdateProfile(userId, name, request.Bio is not null,
            request.Bio is { Length: 0 } ? null : request.Bio, request.AllowTagSearch);
    }

    public Task UpdateProfileWithAvatar(long userId, UpdateProfileRequest request, IFormFile file, CancellationToken cancellation)
    {
        var name = ValidateProfile(request);
        return avatarStorage.Upload(userId, file, cancellation, transaction =>
            users.UpdateProfile(userId, name, request.Bio is not null,
                request.Bio is { Length: 0 } ? null : request.Bio, request.AllowTagSearch, transaction));
    }

    static string? ValidateProfile(UpdateProfileRequest request)
    {
        var name = request.DisplayName?.Trim();
        if (name is { Length: 0 or > 32 })
            throw AppException.BadRequest("display_name must be 1-32 characters");
        if (request.Bio is { Length: > 500 })
            throw AppException.BadRequest("bio must be at most 500 characters");
        if (name is null && request.Bio is null && request.AllowTagSearch is null)
            throw AppException.BadRequest("provide display_name, bio, and/or allow_tag_search");
        return name;
    }
}
