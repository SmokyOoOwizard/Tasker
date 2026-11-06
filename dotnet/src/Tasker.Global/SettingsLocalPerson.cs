using System.Security.Cryptography;
using System.Text;
using Tasker.Core.Users;

namespace Tasker.Global;

public sealed class SettingsLocalPerson(SettingsStore settings, TimeProvider time) : ILocalPerson
{
    public Task<LocalPerson?> Current(CancellationToken ct = default)
    {
        try
        {
            var current = settings.Load();
            var name = current.EffectiveUserName;
            return Task.FromResult<LocalPerson?>(current.LocalUser is { } local
                ? new LocalPerson(local.Id, name, local.CreatedAt)
                : new LocalPerson(IdOf(name), name, time.GetUtcNow()));
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<LocalPerson?>(null);
        }
    }

    public async Task<LocalPerson?> Remember(CancellationToken ct = default)
    {
        try
        {
            var current = await settings.Update(
                x => x.LocalUser == null ? x with { LocalUser = new LocalUserSettings(IdOf(x.EffectiveUserName), time.GetUtcNow()) } : x, ct);
            return new LocalPerson(current.LocalUser!.Id, current.EffectiveUserName, current.LocalUser.CreatedAt);
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Guid IdOf(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"tasker-local-user:{name.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant()}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }
}
