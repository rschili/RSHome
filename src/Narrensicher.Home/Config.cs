using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Narrensicher.Home;
public enum EnvVar
{
    DISCORD_TOKEN,
    DISCORD_ADMIN_ID,
    OPENAI_API_KEY,
    MATRIX_USER_ID,
    MATRIX_PASSWORD,
    SEQ_API_KEY,
    SEQ_URL,
    WEB_LOGIN_HASH,
    WEB_KEY_STORE,
    SQLITE_DB_PATH,
    CERTIFICATE_PATH,
    PFX_PASSWORD,
}

public class Config
{
    private readonly Dictionary<EnvVar, string> _variables;

    public string DiscordToken => _variables[EnvVar.DISCORD_TOKEN];
    public ulong DiscordAdminId => ulong.Parse(_variables[EnvVar.DISCORD_ADMIN_ID]);
    public string OpenAiApiKey => _variables[EnvVar.OPENAI_API_KEY];
    public string MatrixUserId => _variables[EnvVar.MATRIX_USER_ID];
    public string MatrixPassword => _variables[EnvVar.MATRIX_PASSWORD];
    public string SeqApiKey => _variables[EnvVar.SEQ_API_KEY];
    public string SeqUrl => _variables[EnvVar.SEQ_URL];
    public string WebLoginHash => _variables[EnvVar.WEB_LOGIN_HASH];
    public string WebKeyStore => _variables[EnvVar.WEB_KEY_STORE];
    public string SqliteDbPath => _variables[EnvVar.SQLITE_DB_PATH];
    public string CertificatePath => _variables[EnvVar.CERTIFICATE_PATH];
    public string PfxPassword => _variables[EnvVar.PFX_PASSWORD];

    private Config(Dictionary<EnvVar, string> variables)
    {
        _variables = variables;
    }

    public static Config LoadFromEnvFile()
    {
        DotNetEnv.Env.TraversePath().Load();

        var requiredVariables = Enum.GetValues<EnvVar>()
            .ToDictionary(e => e, e => {
                var str = Environment.GetEnvironmentVariable(e.ToString());
                if(string.IsNullOrWhiteSpace(str))
                    throw new KeyNotFoundException($"Environment variable {e} is not set");
                return str;
            });

        return new Config(requiredVariables);
    }
}