using System;
using Enqueuer.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Enqueuer.Logging
{
    // Text for a person at a terminal, JSON for Log Analytics (#112, Issue 4 design decision 4, ENQ-ADR-002). The
    // start-up logger and the host's logger both ask this class, so the two never differ. As RepoScanner's
    // ConsoleLogFormat, with Enqueuer: for Scanner:.
    public static class ConsoleLogFormat
    {
        // Enqueuer:Logging:Format, checked by LoggingOptions
        public const string SettingName = LoggingOptions.SectionName + ":Format";

        // Text or Json in any case wins, as .NET settings usually ignore case. Anything else is left to LoggingOptions
        // to reject (exit 2), and until then, as when it is not set, Development gets text and every other environment,
        // Azure's Production included, gets JSON.
        public static bool IsJson(string? setting, string environmentName)
        {
            if (string.Equals(setting, "Json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(setting, "Text", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        }

        // For the start-up logger, which exists before the host reads configuration: the setting from environment
        // variables and the command line, and the environment as the host takes it, from --environment, else
        // DOTNET_ENVIRONMENT, else Production. appsettings files are not read.
        public static bool IsJsonBeforeTheHost(string[] args)
        {
            IConfiguration commandLine = new ConfigurationBuilder().AddCommandLine(args).Build();
            IConfiguration settings = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();

            string environmentName = commandLine["environment"]
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environments.Production;

            return IsJson(settings[SettingName], environmentName);
        }
    }
}
