using System;
using System.IO;
using Enqueuer.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Enqueuer.Tests.Configuration
{
    [TestFixture]
    public class AzureDevOpsOptionsTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's self-scan
        private const string Pat = "your-test-pat-0001";

        private const string BadOrganizationUrl =
            "Enqueuer:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name, query string or fragment in it";

        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<AzureDevOpsOptions> _validator = new DataAnnotationValidateOptions<AzureDevOpsOptions>(null);

        [TestCase("https://dev.azure.com/BlacklightInternalsMo")]
        [TestCase("https://dev.azure.com/BlacklightInternalsMo/")]
        public void Validate_WithAnHttpsUrlAndAPat_Succeeds(string organizationUrl)
        {
            ValidateOptionsResult result = _validator.Validate(null, new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = Pat });

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        // The one log line says exactly what to set, and where (Issue 2 design, Settings)
        [TestCase("")]
        [TestCase("   ")]
        public void Validate_WithoutAPat_SaysWhatToSet(string pat)
        {
            ValidateOptionsResult result = _validator.Validate(
                null, new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = pat });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Enqueuer:AzureDevOps:Pat").And.Contain("Enqueuer__AzureDevOps__Pat"));
            }
        }

        // Review finding 5: a space or line break copied with the token would be sent with it, and Azure DevOps'
        // refusal would then read as an expired PAT. Said at start-up instead, without showing the value.
        [TestCase(Pat + " ")]
        [TestCase(" " + Pat)]
        [TestCase(Pat + "\r\n")]
        [TestCase("\"" + Pat + "\"")]
        public void Validate_WithSpacesOrQuotesAroundThePat_SaysSoWithoutShowingIt(string pat)
        {
            ValidateOptionsResult result = _validator.Validate(
                null, new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = pat });
            bool leaked = result.FailureMessage?.Contains(Pat, StringComparison.Ordinal) == true;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain(
                    "Enqueuer:AzureDevOps:Pat starts or ends with a space, line break or quote mark. Set it again without them."));
                Assert.That(leaked, Is.False, "the PAT is in the failure message");
            }
        }

        // https only, so the PAT is never sent in the clear. The tests fake the HTTP handler, so unlike the
        // scanner there is no file: URL to allow.
        [TestCase("")]
        [TestCase("dev.azure.com/BlacklightInternalsMo")]
        [TestCase("http://dev.azure.com/BlacklightInternalsMo")]
        [TestCase("file:///C:/repos")]
        // The address Azure DevOps' Clone dialog offers has the user name in it
        [TestCase("https://BlacklightInternalsMo@dev.azure.com/BlacklightInternalsMo")]
        // Review finding 4: the client adds the API path after the URL, so a query string or fragment would put the
        // path in the wrong place
        [TestCase("https://dev.azure.com/BlacklightInternalsMo?x=1")]
        [TestCase("https://dev.azure.com/BlacklightInternalsMo#top")]
        public void Validate_WithABadOrganizationUrl_FailsWithoutShowingThePat(string organizationUrl)
        {
            ValidateOptionsResult result = _validator.Validate(null, new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = Pat });
            bool leaked = result.FailureMessage?.Contains(Pat, StringComparison.Ordinal) == true;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain(BadOrganizationUrl));
                Assert.That(leaked, Is.False, "the PAT is in the failure message");
            }
        }

        // A C# record's ToString prints every property, so a settings object in a log line would print the PAT
        [Test]
        public void ToString_HidesThePat()
        {
            string text = new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = Pat }.ToString();
            bool leaked = text.Contains(Pat, StringComparison.Ordinal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(text, Does.Contain("OrganizationUrl = https://dev.azure.com/BlacklightInternalsMo"));
                Assert.That(text, Does.Contain("Pat = ***"));
                Assert.That(leaked, Is.False, "ToString printed the PAT");
            }
        }

        // Decision 2: locally the enqueuer lists the organisation the scanner clones from. It is not a secret, so
        // it is in appsettings.Development.json; the PAT is not.
        [Test]
        public void CreateBuilder_InDevelopment_ListsBlacklightInternalsMo()
        {
            string[] args =
            {
                "--environment", "Development",
                "--Enqueuer:AzureDevOps:Pat", Pat
            };

            using (IHost host = Program.CreateBuilder(args).Build())
            {
                AzureDevOpsOptions options = host.Services.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;

                Assert.That(options.OrganizationUrl, Is.EqualTo("https://dev.azure.com/BlacklightInternalsMo"));
            }
        }

        // RepoScanner ADR-007: no appsettings file ever holds the PAT. The files are copied next to the tests
        // with the app.
        [Test]
        public void AppSettingsFiles_HaveNoPat()
        {
            string[] files = Directory.GetFiles(AppContext.BaseDirectory, "appsettings*.json");
            Assert.That(files, Is.Not.Empty);

            foreach (string file in files)
            {
                IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

                Assert.That(configuration.GetSection(AzureDevOpsOptions.SectionName + ":Pat").Exists(), Is.False, Path.GetFileName(file));
            }
        }
    }
}
