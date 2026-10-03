using System.Data;

namespace Bento.Api.Db;

public interface IDbPool
{
    Task<IDbConnection> GetConnectionAsync();
    DatabaseProvider Provider { get; }
}

public interface ISystemPool : IDbPool { }
public interface IDataPool : IDbPool { }
