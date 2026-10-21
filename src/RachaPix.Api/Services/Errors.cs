using System.ComponentModel.DataAnnotations;

namespace RachaPix.Api.Services;

/// <summary>Base class for errors that map to a ProblemDetails response.</summary>
public abstract class AppException(int status, string title, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
}

/// <summary>400 with an `errors` dictionary (field → messages).</summary>
public sealed class ValidationException : AppException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base(400, "One or more validation errors occurred.", string.Join(" ", errors.SelectMany(e => e.Value)))
    {
        Errors = errors;
    }
}

public sealed class NotFoundException(string detail) : AppException(404, "Not Found", detail);

public sealed class ForbiddenException(string detail) : AppException(403, "Forbidden", detail);

public sealed class ConflictException(string detail) : AppException(409, "Conflict", detail);

/// <summary>Collects field errors and throws a single <see cref="ValidationException"/>.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public bool Any => _errors.Count > 0;
    public IReadOnlyDictionary<string, List<string>> Errors => _errors;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
    }

    public void ThrowIfAny()
    {
        if (Any) throw new ValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }

    public IEnumerable<ValidationResult> ToValidationResults()
    {
        foreach (var (field, messages) in _errors)
        {
            foreach (var message in messages)
            {
                yield return new ValidationResult(message, [field]);
            }
        }
    }
}
