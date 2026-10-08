using System;
using Enqueuer.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Enqueuer.Tests.Configuration
{
    [TestFixture]
    public class QueueOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<QueueOptions> _validator = new DataAnnotationValidateOptions<QueueOptions>(null);

        [Test]
        public void Validate_WithNameAndConnectionString_Succeeds()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [Test]
        public void Validate_WithNameAndServiceUri_Succeeds()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ServiceUri = new Uri("https://example.queue.core.windows.net") };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [Test]
        public void Validate_WithoutName_FailsNamingTheSetting()
        {
            QueueOptions options = new QueueOptions { ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain(nameof(QueueOptions.Name)));
        }

        [TestCase("Scan-Nightly")]
        [TestCase("scan_nightly")]
        [TestCase("ab")]
        public void Validate_WithInvalidQueueName_Fails(string name)
        {
            QueueOptions options = new QueueOptions { Name = name, ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
        }

        [Test]
        public void Validate_WithBothConnectionStringAndServiceUri_Fails()
        {
            QueueOptions options = new QueueOptions
            {
                Name = "scan-nightly",
                ConnectionString = "UseDevelopmentStorage=true",
                ServiceUri = new Uri("https://example.queue.core.windows.net")
            };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain("Exactly one"));
        }

        [Test]
        public void Validate_WithNeitherConnectionStringNorServiceUri_Fails()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain("Exactly one"));
        }
    }
}
