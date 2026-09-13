using PBIExplorer.Services;

namespace PBIExplorer.Tests;

public class SensitiveTextGuardTests
{
    [Theory]
    [InlineData("Contacto: nacho@empresa.com", "Contacto: ***@***")]
    [InlineData("Cliente[Email] = \"juan.perez@gmail.com\"", "Cliente[Email] = \"***@***\"")]
    [InlineData("Cliente[DNI] = \"12345678\"", "Cliente[DNI] = \"********\"")]
    [InlineData("Cliente[CUIT] = \"20-12345678-9\"", "Cliente[CUIT] = \"**-********-*\"")]
    [InlineData("20-12345678-9", "**-********-*")]
    [InlineData(@"C:\Users\NachoSimone\Documents\ventas.xlsx", @"C:\Users\***\Documents\ventas.xlsx")]
    public void Mask_ScrubsKnownPiiPatterns(string input, string expected)
    {
        Assert.Equal(expected, SensitiveTextGuard.Mask(input));
    }

    [Theory]
    [InlineData("password=SuperSecreto123;")]
    [InlineData("pwd=abc123;")]
    [InlineData("apikey=sk_live_abc123xyz")]
    [InlineData("token=eyJhbGciOiJIUzI1NiJ9")]
    public void Mask_ScrubsOriginalConnLikePatterns(string input)
    {
        var result = SensitiveTextGuard.Mask(input);
        Assert.DoesNotContain("Secreto123", result);
        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("sk_live", result);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", result);
    }

    // Estos son los patrones que el code review marcó como gap real: query params y
    // nombres de credenciales que no llevaban "password"/"apikey" explícito.
    [Theory]
    [InlineData("https://api.datos.gob.ar/series?key=XYZ123SECRET", "XYZ123SECRET")]
    [InlineData("client_id=abc-123-def", "abc-123-def")]
    [InlineData("ClientId=abc-123-def", "abc-123-def")]
    [InlineData("auth=Bearer_xyz789", "Bearer_xyz789")]
    [InlineData("access_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("credential=c0mpl3x", "c0mpl3x")]
    public void Mask_ScrubsExpandedConnLikePatterns(string input, string secretValue)
    {
        var result = SensitiveTextGuard.Mask(input);
        Assert.DoesNotContain(secretValue, result);
        Assert.Contains("=***", result);
    }

    // No debe romper el uso normal de DAX/M con montos, años o palabras que contienen
    // "key" como substring sin ser un parámetro de autenticación.
    [Theory]
    [InlineData("1000000")]
    [InlineData("20241231")]
    [InlineData("SUM(Ventas[Monto]) > 1000000")]
    [InlineData("Ventas[Fecha] >= DATE(2024,1,1)")]
    [InlineData("Monkey business = 5")]
    [InlineData("Turkey[Ventas]")]
    public void Mask_DoesNotTouchUnrelatedText(string input)
    {
        Assert.Equal(input, SensitiveTextGuard.Mask(input));
    }

    [Fact]
    public void Mask_NullOrEmpty_ReturnsSameValue()
    {
        Assert.Null(SensitiveTextGuard.Mask(null!));
        Assert.Equal("", SensitiveTextGuard.Mask(""));
    }

    [Theory]
    [InlineData("password=abc123; token=xyz789; contacto: a@b.com; 20-12345678-9")]
    public void Mask_IsIdempotent(string input)
    {
        var once = SensitiveTextGuard.Mask(input);
        var twice = SensitiveTextGuard.Mask(once);
        Assert.Equal(once, twice);
    }
}
