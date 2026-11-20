using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ExecutionInputProvenanceStorageTests {
  [Fact]
  public void HumanDefaultsAndAgentQuestionPersistAndEnforceProvenance() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), sender = Guid.NewGuid(), run = Guid.NewGuid();
    Guid human = Guid.NewGuid(), question = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.Sessions.CreateSubagent(sender, session, "spawn");
      Assert.True(store.UserMessages.Submit(session, 0,
          new(human, 0, "human", TestLlmSelection.Default), true, run).NewlyAccepted);
      Assert.True(store.UserMessages.Submit(session, 0,
          new(question, 0, "question", TestLlmSelection.Default), true, run, run,
          provenance: InputProvenance.AgentQuestion, senderSessionId: sender).NewlyAccepted);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    AssertProvenance(database, human, 0, null);
    AssertProvenance(database, question, 1, sender);

    // raw-sql: exercise CHECK, NOT NULL and sender admission independently of application validation.
    Assert.Equal(275, Assert.Throws<SqliteException>(() => database.Execute($"""
      UPDATE execution_inputs SET sender_session_id = '{sender:D}' WHERE input_id = '{human:D}';
      """)).SqliteExtendedErrorCode);
    Assert.Equal(275, Assert.Throws<SqliteException>(() => database.Execute($"""
      UPDATE execution_inputs SET sender_session_id = NULL WHERE input_id = '{question:D}';
      """)).SqliteExtendedErrorCode);
    Assert.Equal(275, Assert.Throws<SqliteException>(() => database.Execute($"""
      UPDATE execution_inputs SET provenance = 4 WHERE input_id = '{question:D}';
      """)).SqliteExtendedErrorCode);
    Assert.Equal(1299, Assert.Throws<SqliteException>(() => database.Execute($"""
      UPDATE execution_inputs SET provenance = NULL WHERE input_id = '{question:D}';
      """)).SqliteExtendedErrorCode);
    Assert.Equal(1811, Assert.Throws<SqliteException>(() => database.Execute($"""
      UPDATE execution_inputs SET sender_session_id = '{Guid.NewGuid():D}' WHERE input_id = '{question:D}';
      """)).SqliteExtendedErrorCode);
    AssertProvenance(database, human, 0, null);
    AssertProvenance(database, question, 1, sender);
  }

  private static void AssertProvenance(TemporaryDatabase database, Guid input, long expected, Guid? sender) {
    var row = Assert.Single(database.ReadRows(
        $"SELECT provenance, sender_session_id FROM execution_inputs WHERE input_id = '{input:D}';",
        data => (Provenance: data.GetInt64(0), Sender: data.IsDBNull(1) ? (Guid?)null : Guid.Parse(data.GetString(1))))); // raw-sql: input provenance has no production reader.
    Assert.Equal(expected, row.Provenance);
    Assert.Equal(sender, row.Sender);
  }
}
