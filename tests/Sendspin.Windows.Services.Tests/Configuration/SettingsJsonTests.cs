// <copyright file="SettingsJsonTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json;
using Sendspin.Windows.Services.Configuration;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Configuration;

public class SettingsJsonTests
{
    [Fact]
    public void AcceptsWhatTheConfigurationLoaderAccepts()
    {
        // The app reads settings through Microsoft.Extensions.Configuration, which skips comments
        // and allows trailing commas. A hand-edited file in that shape loads fine, so every writer
        // must be able to update it too.
        const string handEdited = """
            {
              // added by hand
              "App": { "StartMinimized": true, },
            }
            """;

        var root = SettingsJson.Parse(handEdited);

        Assert.True(root["App"]!["StartMinimized"]!.GetValue<bool>());
    }

    [Fact]
    public void UnreadableTextThrows_RatherThanReadingAsEmpty()
    {
        // Reading an unreadable file as an empty object is what let a save replace the user's
        // whole settings file with the one setting being changed.
        Assert.ThrowsAny<JsonException>(() => SettingsJson.Parse("{ \"App\": { \"StartMinimized\": tru"));
    }

    [Fact]
    public void ANonObjectRootThrows()
    {
        Assert.ThrowsAny<JsonException>(() => SettingsJson.Parse("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void AnEmptyFileReadsAsEmpty(string text)
    {
        // Nothing to lose by writing over it, and a write interrupted at the wrong moment can
        // leave a file this way.
        Assert.Empty(SettingsJson.Parse(text));
    }
}
