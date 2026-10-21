namespace DocuMind.Application.Common.Exceptions;

public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.");
