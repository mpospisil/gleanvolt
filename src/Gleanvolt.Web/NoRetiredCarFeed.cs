using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web;

/// <summary>No built-in car feed to retire (#477): for a host without Gleanvolt.Hosting's migration.</summary>
internal sealed class NoRetiredCarFeed : IRetiredCarFeed
{
    public CarFeedPlugin? Notice => null;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
