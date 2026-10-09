using AuthProject.Entities;

namespace AuthProject.Security.Interfaces;

public enum PasswordCheck { Failed, Success, NeedsRehash }

public interface IPasswordHashService
{
    string Hash(User user, string password);
    PasswordCheck Verify(User user, string hash, string password);
}
