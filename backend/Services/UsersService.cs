using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

public interface IUsersService
{
    Task<ProfileResponse> GetProfile(long userId);
    Task<ProfileResponse> GetProfileById(long userId);
    Task<List<UserCardResponse>> Search(string query);
    Task UpdateProfile(long userId, UpdateProfileRequest request);
}

public sealed class UsersService(IUsersRepository users, IAvatarsRepository avatars) : IUsersService
{
    public Task<ProfileResponse> GetProfile(long userId) => GetProfileById(userId);

    public async Task<ProfileResponse> GetProfileById(long userId)
    {
        var user = await users.GetById(userId) ?? throw AppException.NotFound("user not found");
        var avatar = (await avatars.GetByUserIds([userId])).GetValueOrDefault(userId);
        return ProfileResponse.From(user, avatar?.Path);
    }

    public async Task<List<UserCardResponse>> Search(string query)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0) throw AppException.BadRequest("query is required");
        return (await users.Search(normalized, 20)).Select(UserCardResponse.From).ToList();
    }

    public async Task UpdateProfile(long userId, UpdateProfileRequest request)
    {
        var name = request.DisplayName?.Trim();
        if (name is { Length: 0 or > 32 })
            throw AppException.BadRequest("display_name must be 1-32 characters");
        if (request.Bio is { Length: > 500 })
            throw AppException.BadRequest("bio must be at most 500 characters");
        if (name is null && request.Bio is null)
            throw AppException.BadRequest("provide display_name and/or bio");
        await users.UpdateProfile(userId, name, request.Bio is not null,
            request.Bio is { Length: 0 } ? null : request.Bio);
    }
}
