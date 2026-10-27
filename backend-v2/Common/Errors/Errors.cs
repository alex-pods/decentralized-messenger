namespace Messenger.Api.Common.Errors;

public sealed class NotFoundException(string message) : Exception(message);
public sealed class ForbiddenException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);
public sealed class ValidationException(string message) : Exception(message);
