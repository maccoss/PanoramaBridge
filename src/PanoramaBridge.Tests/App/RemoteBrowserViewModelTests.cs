using System.Net;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The remote folder picker.
/// </summary>
/// <remarks>
/// Two things matter here and both are about not wasting somebody's afternoon: folders load when
/// they are opened rather than all at once, because the MacCoss project alone has around sixty
/// sub-containers; and a read-only folder is refused at the moment it is chosen rather than
/// surfacing as a 403 hours into a transfer.
/// </remarks>
public sealed class RemoteBrowserViewModelTests
{
    private static readonly RemotePath Root = RemotePath.Parse("/_webdav/");

    private static FakeWebDavClient Server()
    {
        var server = new FakeWebDavClient();

        // A project the account can write to, one it cannot, and a hidden one.
        server.Seed(RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/run.raw"), [1, 2, 3]);
        server.Seed(RemotePath.Parse("/_webdav/Shared/@files/other.raw"), [4, 5, 6]);
        server.Seed(RemotePath.Parse("/_webdav/.hidden/@files/secret.raw"), [7]);

        return server;
    }

    [Fact]
    public async Task The_root_lists_the_projects_this_account_can_see()
    {
        var view = new RemoteBrowserViewModel(Server(), "/_webdav/MacCoss/maccoss/@files/");

        await view.InitializeAsync();

        view.IsLoading.ShouldBeFalse();
        view.Error.ShouldBeNull();
        view.Roots.Select(r => r.Name).ShouldContain("MacCoss");
    }

    [Fact]
    public async Task A_folder_beginning_with_a_dot_is_not_shown()
    {
        // Working directories, not data. Showing them invites somebody to upload into one.
        var view = new RemoteBrowserViewModel(Server(), "/_webdav/");

        await view.InitializeAsync();

        view.Roots.Select(r => r.Name).ShouldNotContain(".hidden");
    }

    [Fact]
    public async Task Nothing_below_a_folder_is_fetched_until_it_is_opened()
    {
        // The cost assertion. Eagerly walking the tree to draw one level would be thousands of
        // requests, and it is exactly what a tree control invites you to write.
        var server = Server();
        var view = new RemoteBrowserViewModel(server, "/_webdav/");

        await view.InitializeAsync();

        server.ListCalls.ShouldBe(1, "one listing draws the whole first level");

        var project = view.Roots.Single(r => r.Name == "MacCoss");
        project.Children.ShouldHaveSingleItem();
        project.Children[0].IsPlaceholder.ShouldBeTrue("so the node can be expanded at all");

        await project.LoadChildrenAsync();

        server.ListCalls.ShouldBe(2, "and one more when a node is actually opened");
        project.Children.ShouldNotContain(c => c.IsPlaceholder);
    }

    [Fact]
    public async Task Opening_a_folder_twice_fetches_it_once()
    {
        var server = Server();
        var view = new RemoteBrowserViewModel(server, "/_webdav/");

        await view.InitializeAsync();

        var project = view.Roots.Single(r => r.Name == "MacCoss");

        await project.LoadChildrenAsync();
        await project.LoadChildrenAsync();

        server.ListCalls.ShouldBe(2);
    }

    [Fact]
    public async Task A_read_only_folder_cannot_be_chosen()
    {
        // The whole reason permissions are read from the same listing that draws the tree.
        var server = Server();
        server.ReadOnlyPaths.Add("/_webdav/Shared/");

        var view = new RemoteBrowserViewModel(server, "/_webdav/");
        await view.InitializeAsync();

        view.Selected = view.Roots.Single(r => r.Name == "Shared");

        view.CanAccept.ShouldBeFalse();
        view.AcceptHint.ShouldContain("read-only");

        view.Selected = view.Roots.Single(r => r.Name == "MacCoss");

        view.CanAccept.ShouldBeTrue();
        view.SelectedPath.ShouldBe("/_webdav/MacCoss/");
    }

    [Fact]
    public void Nothing_chosen_is_not_an_error_it_is_an_instruction()
    {
        var view = new RemoteBrowserViewModel(Server(), "/_webdav/");

        view.CanAccept.ShouldBeFalse();
        view.AcceptHint.ShouldBe("Choose a folder.");
    }

    [Fact]
    public async Task A_folder_that_fails_to_open_can_be_tried_again()
    {
        // A transient failure must not leave a node permanently unopenable, and the message has
        // to be the one written for a person rather than a status code.
        var server = Server();
        var view = new RemoteBrowserViewModel(server, "/_webdav/");

        await view.InitializeAsync();

        var project = view.Roots.Single(r => r.Name == "MacCoss");

        server.FailListingsOnce = true;
        await project.LoadChildrenAsync();

        project.Error.ShouldNotBeNull();
        project.Children.ShouldBeEmpty();

        await project.LoadChildrenAsync();

        project.Error.ShouldBeNull();
        project.Children.ShouldNotBeEmpty("the second attempt was allowed");
    }

    [Fact]
    public async Task A_root_that_cannot_be_listed_is_explained_rather_than_left_blank()
    {
        var server = Server();
        server.FailListingsOnce = true;

        var view = new RemoteBrowserViewModel(server, "/_webdav/");
        await view.InitializeAsync();

        view.IsLoading.ShouldBeFalse();
        view.Error.ShouldNotBeNull();
        view.Roots.ShouldBeEmpty();
    }
}
