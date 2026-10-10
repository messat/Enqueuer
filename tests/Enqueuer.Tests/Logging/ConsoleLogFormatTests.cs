using System;
using Enqueuer.Logging;
using NUnit.Framework;

namespace Enqueuer.Tests.Logging
{
    // Issue 4 design, decision 4: text for a person at a terminal, JSON for Log Analytics, decided the same way for the
    // start-up logger and the host's. As RepoScanner's ConsoleLogFormatTests, with Enqueuer: for Scanner:.
    [TestFixture]
    public class ConsoleLogFormatTests
    {
        [TestCase(null, "Development", false)]
        [TestCase(null, "Production", true)]
        [TestCase(null, "Staging", true)]
        // The environment every ProgramTests run uses
        [TestCase(null, "Test", true)]
        [TestCase("Text", "Production", false)]
        [TestCase("Json", "Development", true)]
        // .NET settings usually ignore case, so json and TEXT are taken as Json and Text
        [TestCase("json", "Development", true)]
        [TestCase("TEXT", "Production", false)]
        // LoggingOptions rejects any other value (exit 2); until then, the environment's default
        [TestCase("Xml", "Production", true)]
        [TestCase("Xml", "Development", false)]
        public void IsJson_ChoosesBySettingThenEnvironment(string? setting, string environment, bool expected)
        {
            Assert.That(ConsoleLogFormat.IsJson(setting, environment), Is.EqualTo(expected));
        }

        // The start-up logger exists before configuration is read, so it reads the command line itself
        [TestCase(new[] { "--environment", "Development" }, false)]
        [TestCase(new[] { "--environment", "Production" }, true)]
        [TestCase(new[] { "--environment", "Development", "--Enqueuer:Logging:Format", "Json" }, true)]
        [TestCase(new[] { "--environment", "Production", "--Enqueuer:Logging:Format", "text" }, false)]
        public void IsJsonBeforeTheHost_ReadsTheCommandLine(string[] args, bool expected)
        {
            Assert.That(ConsoleLogFormat.IsJsonBeforeTheHost(args), Is.EqualTo(expected));
        }

        // The host takes the environment from --environment, else DOTNET_ENVIRONMENT, so the start-up logger must too
        [Test]
        public void IsJsonBeforeTheHost_WithoutAnEnvironmentArgument_UsesDotnetEnvironment()
        {
            string? before = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            try
            {
                Assert.That(ConsoleLogFormat.IsJsonBeforeTheHost(new string[0]), Is.False);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", before);
            }
        }

        [Test]
        public void IsJsonBeforeTheHost_WithAnEnvironmentArgument_PrefersItToDotnetEnvironment()
        {
            string? before = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            try
            {
                Assert.That(ConsoleLogFormat.IsJsonBeforeTheHost(new[] { "--environment", "Test" }), Is.True);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", before);
            }
        }
    }
}
