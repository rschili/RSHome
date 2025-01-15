using System.Threading.Tasks;
using RSHome.Services;

namespace RSHome.Tests;

public class OpenAIServiceTests
{
    [Test]
        public async Task SanitizeName_RemovesInvalidCharacters()
        {
            var replacedParticipantNames = new Dictionary<string, string>();
            string input = "Invalid@Name!";
            string expected = "InvalidName";

            string result = OpenAIService.SanitizeName(input, replacedParticipantNames);

            await Assert.That(result).IsEqualTo(expected);
        }

        [Test]
        public async Task SanitizeName_NormalizesUnicodeCharacters()
        {
            var replacedParticipantNames = new Dictionary<string, string>();
            string input = "Náïve";
            string expected = "Naive";

            string result = OpenAIService.SanitizeName(input, replacedParticipantNames);

            await Assert.That(result).IsEqualTo(expected);
        }

        [Test]
        [Arguments("Gustaff Pfiffikus", "GustaffPfiffikus")]
        [Arguments("Das Serum", "DasSerum")]
        [Arguments("sikk 🦀", "sikk")]
        public async Task SanitizeName_HandlesKnownNames(string input, string expected)
        {
            var replacedParticipantNames = new Dictionary<string, string>();

            string result = OpenAIService.SanitizeName(input, replacedParticipantNames);
            await Assert.That(result).IsEqualTo(expected);
        }

        [Test]
        [Arguments("Gustaff Pfiffikus", "GustaffPfiffikus")]
        [Arguments("Das Serum", "DasSerum")]
        [Arguments("sikk 🦀", "sikk")]
        public async Task RestoreNames_HandlesKnownNames(string input, string expected)
        {
            var replacedParticipantNames = new Dictionary<string, string>();

            string result = OpenAIService.SanitizeName(input, replacedParticipantNames);
            await Assert.That(result).IsEqualTo(expected);

            string response = $"[[{expected}]]: Ja das stimmt, {expected}!";
            string restoredResponse = OpenAIService.RestoreNames(response, replacedParticipantNames);
            await Assert.That(restoredResponse).IsEqualTo($"[[{input}]]: Ja das stimmt, {input}!");
        }

        [Test]
        public async Task SanitizeName_HandlesEmptyString()
        {
            var replacedParticipantNames = new Dictionary<string, string>();
            string input = "";
            string expected = "";

            string result = OpenAIService.SanitizeName(input, replacedParticipantNames);

            await Assert.That(result).IsEqualTo(expected);
        }

        [Test]
        public async Task SanitizeName_HandlesNullString()
        {
            var replacedParticipantNames = new Dictionary<string, string>();
            await Assert.That(() => OpenAIService.SanitizeName(null!, replacedParticipantNames)).Throws<ArgumentNullException>();
        }

        [Test]
        public async Task SanitizeName_HandlesNullDictionary()
        {
            await Assert.That(() => OpenAIService.SanitizeName("test", null!)).Throws<ArgumentNullException>();
        }

        [Test]
        public async Task SanitizeName_CachesSanitizedNames()
        {
            var replacedParticipantNames = new Dictionary<string, string>();
            string input = "NameWith@Special#Characters";
            string expected = "NameWithSpecialCharacters";

            string result1 = OpenAIService.SanitizeName(input, replacedParticipantNames);
            string result2 = OpenAIService.SanitizeName(input, replacedParticipantNames);

            await Assert.That(result1).IsEqualTo(expected);
            await Assert.That(result2).IsEqualTo(expected);
            await Assert.That(replacedParticipantNames.Count).IsEqualTo(1);
        }

        [Test]
        public async Task SanitizeName_AllowsValidCharacters()
        {
            var replacedParticipantNames = new Dictionary<string, string>();
            string input = "Valid_Name-123";
            string expected = "Valid_Name-123";

            string result = OpenAIService.SanitizeName(input, replacedParticipantNames);

            await Assert.That(result).IsEqualTo(expected);
        }

}