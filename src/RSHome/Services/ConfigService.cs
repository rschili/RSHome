using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RSHome.Services;

public interface IConfigService
{
    string SeqApiKey { get; }
    string SeqUrl { get; }
    string WebLoginHash { get; }
    string WebKeyStore { get; }
    string CertificatePath { get; }
    string PfxPassword { get; }
}


public enum EnvVar
{
    SEQ_API_KEY,
    SEQ_URL,
    WEB_LOGIN_HASH,
    WEB_KEY_STORE,
    CERTIFICATE_PATH,
    PFX_PASSWORD,
}

public class ConfigService : IConfigService
{
    private readonly Dictionary<EnvVar, string> _variables;

    public string SeqApiKey => _variables[EnvVar.SEQ_API_KEY];
    public string SeqUrl => _variables[EnvVar.SEQ_URL];
    public string WebLoginHash => _variables[EnvVar.WEB_LOGIN_HASH];
    public string WebKeyStore => _variables[EnvVar.WEB_KEY_STORE];
    public string CertificatePath => _variables[EnvVar.CERTIFICATE_PATH];
    public string PfxPassword => _variables[EnvVar.PFX_PASSWORD];

    private ConfigService(Dictionary<EnvVar, string> variables)
    {
        _variables = variables;
    }

    public static ConfigService LoadFromEnvFile()
    {
        DotNetEnv.Env.TraversePath().Load();

        var requiredVariables = Enum.GetValues<EnvVar>()
            .ToDictionary(e => e, e =>
            {
                var str = Environment.GetEnvironmentVariable(e.ToString());
                if (string.IsNullOrWhiteSpace(str))
                    throw new KeyNotFoundException($"Environment variable {e} is not set");
                return str;
            });

        return new ConfigService(requiredVariables);
    }
}