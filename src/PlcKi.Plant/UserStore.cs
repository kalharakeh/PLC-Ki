using System.Security.Cryptography;
using System.Text.Json;

namespace PlcKi.Plant;

public sealed record PlantUser(string Name, string Role, string Hash, string Salt, int Iterations, string Stamp, DateTimeOffset Created);

/// <summary>
/// The people who may log in, kept in users.json in the data folder. Passwords are stored only as
/// PBKDF2-SHA256 hashes with a random salt. Three roles (what each may do: <see cref="Roles"/>):
///   admin      - everything, including settings and users
///   technician - also notes, documents and values on PLCSIM; no settings, no real PLC
///   operator   - all machine views and the assistant, no settings
/// </summary>
public sealed class UserStore
{
    public const string Admin = Roles.Admin;
    public const string Technician = Roles.Technician;
    public const string Operator = Roles.Operator;
    public const int MinPasswordLength = 8;
    private const int Iterations = 210_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private readonly object _lock = new();
    private List<PlantUser> _users;

    public UserStore(string file)
    {
        _file = file;
        _users = File.Exists(file)
            ? JsonSerializer.Deserialize<List<PlantUser>>(File.ReadAllText(file), Json) ?? new()
            : new();
    }

    public bool Any { get { lock (_lock) return _users.Count > 0; } }

    public IReadOnlyList<PlantUser> List() { lock (_lock) return _users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList(); }

    public PlantUser? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock) return _users.FirstOrDefault(u => u.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The user if the password is right, otherwise null. Takes the same time for unknown names.</summary>
    public PlantUser? Verify(string? name, string? password)
    {
        var user = Find(name);
        var salt = user != null ? Convert.FromBase64String(user.Salt) : new byte[16];
        var expected = user != null ? Convert.FromBase64String(user.Hash) : new byte[32];
        var actual = Hash(password ?? "", salt, user?.Iterations ?? Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && user != null ? user : null;
    }

    public PlantUser Add(string name, string password, string role)
    {
        name = CheckName(name);
        role = CheckRole(role);
        CheckPassword(password);
        lock (_lock)
        {
            if (Find(name) != null) throw new PlantException($"There is already a user called '{name}'.", 409);
            var user = Create(name, password, role, DateTimeOffset.Now);
            Save(_users.Append(user).ToList());
            return user;
        }
    }

    public void Delete(string name, string currentUser)
    {
        lock (_lock)
        {
            var user = Find(name) ?? throw new PlantException($"There is no user called '{name}'.", 404);
            if (user.Name.Equals(currentUser, StringComparison.OrdinalIgnoreCase))
                throw new PlantException("You cannot delete yourself. Ask another administrator.");
            if (user.Role == Admin && _users.Count(u => u.Role == Admin) == 1)
                throw new PlantException("This is the last administrator. Make someone else administrator first.");
            Save(_users.Where(u => u != user).ToList());
        }
    }

    public void SetRole(string name, string role, string currentUser)
    {
        role = CheckRole(role);
        lock (_lock)
        {
            var user = Find(name) ?? throw new PlantException($"There is no user called '{name}'.", 404);
            if (user.Role == role) return;
            if (user.Role == Admin && _users.Count(u => u.Role == Admin) == 1)
                throw new PlantException("This is the last administrator. Make someone else administrator first.");
            if (user.Name.Equals(currentUser, StringComparison.OrdinalIgnoreCase))
                throw new PlantException("You cannot change your own role. Ask another administrator.");
            Replace(user, user with { Role = role, Stamp = NewStamp() });
        }
    }

    /// <summary>Sets a new password and logs the user out everywhere else.</summary>
    public PlantUser SetPassword(string name, string password)
    {
        CheckPassword(password);
        lock (_lock)
        {
            var user = Find(name) ?? throw new PlantException($"There is no user called '{name}'.", 404);
            var updated = Create(user.Name, password, user.Role, user.Created);
            Replace(user, updated);
            return updated;
        }
    }

    private void Replace(PlantUser old, PlantUser updated) => Save(_users.Select(u => u == old ? updated : u).ToList());

    private void Save(List<PlantUser> users)
    {
        AtomicFile.Save(_file, JsonSerializer.Serialize(users, Json));
        _users = users;
    }

    private static PlantUser Create(string name, string password, string role, DateTimeOffset created)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new PlantUser(name, role, Convert.ToBase64String(Hash(password, salt, Iterations)),
            Convert.ToBase64String(salt), Iterations, NewStamp(), created);
    }

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string CheckName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 40 || !name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' '))
            throw new PlantException("A user name has 1 to 40 letters, digits, spaces, dots, dashes or underscores.");
        return name;
    }

    private static string CheckRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        Admin => Admin,
        Technician => Technician,
        Operator => Operator,
        _ => throw new PlantException("The role is 'admin', 'technician' or 'operator'."),
    };

    private static void CheckPassword(string? password)
    {
        if ((password ?? "").Length < MinPasswordLength)
            throw new PlantException($"A password needs at least {MinPasswordLength} characters.");
    }
}

/// <summary>Slows down password guessing: after 5 wrong passwords for a name, that name is locked for a minute.</summary>
public sealed class LoginThrottle
{
    private readonly Dictionary<string, (int Fails, DateTimeOffset Until)> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public LoginThrottle(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public TimeSpan? Blocked(string name)
    {
        lock (_state)
            return _state.TryGetValue(name, out var s) && s.Until > _now() ? s.Until - _now() : null;
    }

    public void Failed(string name)
    {
        lock (_state)
        {
            var fails = (_state.TryGetValue(name, out var s) ? s.Fails : 0) + 1;
            _state[name] = fails >= 5 ? (0, _now().AddMinutes(1)) : (fails, DateTimeOffset.MinValue);
        }
    }

    public void Succeeded(string name)
    {
        lock (_state) _state.Remove(name);
    }
}

internal static class AtomicFile
{
    /// <summary>Writes to a temporary file first and then swaps it in, so a crash never leaves half a file.</summary>
    public static void Save(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
