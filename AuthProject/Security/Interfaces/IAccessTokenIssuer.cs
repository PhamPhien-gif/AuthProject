using AuthProject.Entities;

namespace AuthProject.Security.Interfaces;

public interface IAccessTokenIssuer
{
    string Issue(AuthSession session);
}
