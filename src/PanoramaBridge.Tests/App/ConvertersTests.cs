using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PanoramaBridge.App.Views;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The value converters the XAML binds through.
/// </summary>
/// <remarks>
/// Small enough to look beneath testing, and the one place a mistake is invisible: a converter
/// that returns the wrong thing produces a window that looks plausible and behaves wrongly, with
/// nothing in any log. The enum converter is the one that matters -- it writes back to a real
/// setting from a radio group.
/// </remarks>
public sealed class ConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void The_inverse_visibility_converter_hides_on_true()
    {
        var converter = new InverseBooleanToVisibilityConverter();

        converter.Convert(true, typeof(Visibility), null!, Culture).ShouldBe(Visibility.Collapsed);
        converter.Convert(false, typeof(Visibility), null!, Culture).ShouldBe(Visibility.Visible);

        // Anything that is not a true boolean shows, rather than hiding a control because a
        // binding has not resolved yet.
        converter.Convert(null!, typeof(Visibility), null!, Culture).ShouldBe(Visibility.Visible);
    }

    [Fact]
    public void An_unmeasurable_progress_bar_is_hidden_rather_than_shown_empty()
    {
        // A null fraction means the size is unknown. An empty bar reads as "nothing is
        // happening", which is a different and wrong statement.
        var converter = new NullableDoubleToVisibilityConverter();

        converter.Convert(null, typeof(Visibility), null!, Culture).ShouldBe(Visibility.Collapsed);
        converter.Convert(0d, typeof(Visibility), null!, Culture).ShouldBe(Visibility.Visible);
        converter.Convert(0.5d, typeof(Visibility), null!, Culture).ShouldBe(Visibility.Visible);
    }

    [Fact]
    public void The_enum_converter_matches_the_member_it_was_given()
    {
        var converter = new EnumMatchConverter();

        converter.Convert(ConflictPolicy.Ask, typeof(bool), "Ask", Culture).ShouldBe(true);
        converter.Convert(ConflictPolicy.Ask, typeof(bool), "Overwrite", Culture).ShouldBe(false);
        converter.Convert(null, typeof(bool), "Ask", Culture).ShouldBe(false);
    }

    [Fact]
    public void Only_the_radio_button_being_checked_writes_back()
    {
        // A radio group raises ConvertBack for the one being cleared as well as the one being
        // set. Answering with a value for both would leave the setting on whichever unchecked
        // itself last -- which is to say, on the wrong one.
        var converter = new EnumMatchConverter();

        converter.ConvertBack(true, typeof(ConflictPolicy), "Overwrite", Culture)
            .ShouldBe(ConflictPolicy.Overwrite);

        converter.ConvertBack(false, typeof(ConflictPolicy), "Ask", Culture)
            .ShouldBe(Binding.DoNothing);

        converter.ConvertBack(true, typeof(ConflictPolicy), null, Culture)
            .ShouldBe(Binding.DoNothing);
    }

    [Fact]
    public void The_enum_converter_writes_back_through_a_nullable_binding()
    {
        // WPF hands the nullable type through when the bound property is one, and Enum.Parse
        // refuses it.
        new EnumMatchConverter()
            .ConvertBack(true, typeof(ConflictPolicy?), "Skip", Culture)
            .ShouldBe(ConflictPolicy.Skip);
    }
}
