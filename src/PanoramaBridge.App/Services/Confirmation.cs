using System.Windows;

namespace PanoramaBridge.App.Services;

/// <summary>
/// Asking the person at the keyboard a yes-or-no question before something irreversible.
/// </summary>
/// <remarks>
/// <para>
/// Defaulted onto the view models that need it rather than wired by a view, because wiring is a
/// thing that can be forgotten and was: Delete on the Configurations tab removed a configuration
/// without asking, for the whole life of the feature, because nothing ever set the callback the
/// view model politely treated as optional. A null check that means "carry on" turns a missing
/// wire into silent data loss, and nothing on screen says so.
/// </para>
/// <para>
/// Refuses rather than asks when there is no application to ask through, which is every test and
/// every headless host. Two reasons, and both matter: a destructive action that cannot get an
/// answer must not proceed, and a message box in a test run would hang rather than fail. A test
/// that wants the action to happen says so by replacing this, which also makes the intent
/// explicit at the point it is relied on.
/// </para>
/// </remarks>
public static class Confirmation
{
    /// <summary>Puts the question, and answers no when there is nobody to put it to.</summary>
    public static bool Ask(string question) =>
        Application.Current is not null
        && MessageBox.Show(
            question,
            "PanoramaBridge",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
}
