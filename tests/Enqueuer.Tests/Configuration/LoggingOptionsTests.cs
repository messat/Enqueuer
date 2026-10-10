using Enqueuer.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Enqueuer.Tests.Configuration
{
    // Issue 4 design, decision 4: Enqueuer:Logging:Format is Text or Json in any case, and anything else stops the
    // enqueuer at start-up (exit 2) like any other wrong setting
    [TestFixture]
    public class LoggingOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<LoggingOptions> _validator = new DataAnnotationValidateOptions<LoggingOptions>(null);

        // Not set is allowed: ConsoleLogFormat then chooses by environment
        [Test]
        public void Validate_WithFormatNotSet_Succeeds()
        {
            LoggingOptions options = new LoggingOptions();

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [TestCase("Text")]
        [TestCase("json")]
        [TestCase("JSON")]
        public void Validate_WithTextOrJsonInAnyCase_Succeeds(string format)
        {
            LoggingOptions options = new LoggingOptions { Format = format };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [TestCase("Xml")]
        [TestCase("Jsonl")]
        public void Validate_WithAnyOtherFormat_FailsNamingTheSetting(string format)
        {
            LoggingOptions options = new LoggingOptions { Format = format };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Enqueuer:Logging:Format must be Text or Json"));
            }
        }
    }
}
