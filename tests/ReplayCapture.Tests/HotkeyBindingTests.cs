using ReplayCapture.Core.Input;

namespace ReplayCapture.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Alt+F10", HotkeyModifiers.Alt, 0x79u, "Alt+F10")]
    [InlineData("alt+f10", HotkeyModifiers.Alt, 0x79u, "Alt+F10")]
    [InlineData("Ctrl+Shift+S", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x53u, "Ctrl+Shift+S")]
    [InlineData("Win+Alt+R", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x52u, "Win+Alt+R")]
    [InlineData("F12", HotkeyModifiers.None, 0x7Bu, "F12")]
    [InlineData("Ctrl+PrintScreen", HotkeyModifiers.Control, 0x2Cu, "Ctrl+PrintScreen")]
    [InlineData("F24", HotkeyModifiers.None, 0x87u, "F24")]
    public void Parses_supported_combinations(
        string input, HotkeyModifiers expectedModifiers, uint expectedVk, string expectedDisplay)
    {
        var binding = HotkeyBinding.Parse(input);

        // NoRepeat is always forced on so holding the key cannot spam saves.
        Assert.Equal(expectedModifiers | HotkeyModifiers.NoRepeat, binding.Modifiers);
        Assert.Equal(expectedVk, binding.VirtualKey);
        Assert.Equal(expectedDisplay, binding.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alt")]                 // modifier with no key
    [InlineData("S")]                   // bare letter would swallow the key system-wide
    [InlineData("Alt+F10+B")]           // two non-modifier keys
    [InlineData("Ctrl+NotAKey")]
    [InlineData("F25")]                 // past VK_F24
    public void Rejects_unusable_input(string input)
    {
        Assert.False(HotkeyBinding.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Bare_function_keys_are_allowed_but_bare_letters_are_not()
    {
        Assert.True(HotkeyBinding.TryParse("F9", out _, out _));
        Assert.False(HotkeyBinding.TryParse("9", out _, out _));
    }

    [Fact]
    public void Default_is_alt_f10()
    {
        Assert.Equal("Alt+F10", HotkeyBinding.Default.Display);
        Assert.Equal(0x79u, HotkeyBinding.Default.VirtualKey);
        Assert.True(HotkeyBinding.Default.Modifiers.HasFlag(HotkeyModifiers.Alt));
        Assert.True(HotkeyBinding.Default.Modifiers.HasFlag(HotkeyModifiers.NoRepeat));
    }
}
