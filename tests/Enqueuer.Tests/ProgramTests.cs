using System;
using System.IO;
using NUnit.Framework;

namespace Enqueuer.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Program.Main is the placeholder "Hello, World!" until the Generic Host arrives (#105),
        // at which point this test is replaced by host, options and exit code tests.
        [Test]
        public void Main_WithNoArguments_WritesHelloWorld()
        {
            TextWriter originalOut = Console.Out;

            using (StringWriter output = new StringWriter())
            {
                // Capture everything Main writes to the console
                Console.SetOut(output);
                try
                {
                    Program.Main(Array.Empty<string>());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                Assert.That(output.ToString().Trim(), Is.EqualTo("Hello, World!"));
            }
        }
    }
}
