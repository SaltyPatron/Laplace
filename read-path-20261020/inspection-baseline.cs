using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Laplace.SubstrateCRUD.Npgsql;
public static class InspectionBaseline {
 public static async Task Main() {
  await using var conn=new NpgsqlConnection("Host=127.0.0.1;Port=55438;Database=postgres;Username=readpath_validation;SSL Mode=Disable;Pooling=false");await conn.OpenAsync();
  await using var tx=await conn.BeginTransactionAsync();
  const string setup="INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at) SELECT laplace.consensus_id(s,t,o),s,t,o,2176851004240,139542473703,60009621,8,now() FROM (SELECT decode('3e7f0ee67a293f2da9fc17a259a275fe','hex') s,decode('2fd6a1e32aa77b3583a46ca38763050b','hex') t,laplace.word_id('L') o) r";
  await using(var cmd=new NpgsqlCommand(setup,conn))await cmd.ExecuteNonQueryAsync();
  var failed=false;
  try {await NpgsqlSubstrateReads.ConsensusOutRenderedAsync(conn,Convert.FromHexString("3e7f0ee67a293f2da9fc17a259a275fe"),CancellationToken.None);}
  catch(InvalidCastException ex){failed=true;Console.WriteLine(ex.GetType().Name+": "+ex.Message);}
  if(!failed)throw new Exception("Failed to reproduce NULL render boundary on baseline");
  await tx.RollbackAsync();Console.WriteLine("Baseline inspection failure reproduced; private row rolled back.");
 }
}
