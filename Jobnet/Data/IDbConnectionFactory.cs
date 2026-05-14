using System.Data;

namespace Jobnet.Data;

public interface IDbConnectionFactory
{
    IDbConnection Open();
}
