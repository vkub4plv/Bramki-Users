using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using BramkiUsers.ConfigurationQuery;
using BramkiUsers.SystemSynchronization;
using BramkiUsers.Communication;
using Microsoft.Extensions.Logging;

namespace BramkiUsers.Wcf
{
    public sealed class VisoFacadeShared
    {
        private readonly VisoClientFactory _clients;
        private readonly IVisoSessionProvider _session;
        private readonly ILogger<VisoFacadeShared> _log;
		// Set the actual default group ID for new users here
		private const int DefaultGroupId = 1;

        public VisoFacadeShared(
            VisoClientFactory clients,
            IVisoSessionProvider session,
            ILogger<VisoFacadeShared> log)
        {
            _clients = clients;
            _session = session;
            _log = log;
        }

        // --------------------------------------------------------------------
        // Provision (person + credential + factor + partial sync)
        // --------------------------------------------------------------------
        public async Task<(int personId, int credentialId, int factorId)> ProvisionAsync(
            string erpId,
            string firstName,
            string lastName,
            string cardDec,
            GroupData group,
            bool autoFullSyncOnFailure = true)
        {
            _log.LogInformation(
                $"VISO: ProvisionAsync start ERPID={erpId}, FirstName={firstName}, LastName={lastName}, GroupId={group?.ID}");

            var result = await WithSessionRetry(async token =>
            {
                using var cfg = _clients.CreateConfig();
                using var sync = _clients.CreateSync();

                // person
                var personId = await cfg.InsertPersonAsync(new PersonData
                {
                    Name = erpId,
                    FirstName = firstName,
                    LastName = lastName,
                    GroupID = group.ID,
                    UserExternalIdentifier = erpId,
                    UserInformationReference = erpId,
                    DataProcessingPermission = true
                }, token);
                if (personId <= 0) throw await MakeServiceError(cfg, "InsertPerson");

                // credential
                var creds = await cfg.GetPersonCredentialsAsync(personId, token) ?? Array.Empty<CredentialData>();
                var baseName = string.IsNullOrWhiteSpace(erpId) ? "ID" : erpId;
                var credName = UniqueName(creds.Select(c => c.Name).ToArray(), baseName);
                var credentialId = await cfg.InsertCredentialAsync(new CredentialData { Name = credName }, token);
                if (credentialId <= 0) throw await MakeServiceError(cfg, "InsertCredential");

                var arc = await cfg.AssignCredentialsAsync(personId, new[] { credentialId }, token);
                if (arc < 0) throw await MakeServiceError(cfg, "AssignCredentials");

                // factor
                var types = await cfg.GetFactorTypesAsync(token) ?? Array.Empty<FactorTypeData>();
                var cardType =
                       types.FirstOrDefault(ft => string.Equals(ft.Name, "Card40B", StringComparison.OrdinalIgnoreCase))
                    ?? types.FirstOrDefault(ft => ft.ID == 6)
                    ?? types.FirstOrDefault()
                    ?? throw new InvalidOperationException("No FactorType available.");

                var dec = new string((cardDec ?? "").Where(char.IsDigit).ToArray());
                if (string.IsNullOrWhiteSpace(dec)) throw new InvalidOperationException("Card DEC invalid.");

                var factors = await cfg.GetAuthenticationFactorsByCredentialIdAsync(credentialId, token) ?? Array.Empty<FactorData>();
                var factorName = UniqueName(factors.Select(f => f.Name).ToArray(), baseName);
                var factorId = await cfg.InsertAuthenticationFactorAsync(credentialId, new FactorData
                {
                    Name = factorName,
                    Value = dec,
                    FactorTypeID = cardType.ID,
                    Disable = 0,
                    Tag = 0,
                    Index = 0
                }, token);
                if (factorId <= 0) throw await MakeServiceError(cfg, "InsertAuthenticationFactor");

                try
                {
                    int rc = await sync.PartialCredentialsSynchronizationAsync(new[] { credentialId }, token);
                    if (rc != 0 && autoFullSyncOnFailure && (rc is 258 or 262 or 255 or 257 or 259))
                    {
                        _log.LogWarning($"VISO: ProvisionAsync partial sync rc={rc}; running full sync.");
                        await FullSyncAsync(token);
                    }
                }
                catch when (autoFullSyncOnFailure)
                {
                    _log.LogWarning("VISO: ProvisionAsync partial sync threw; running full sync.");
                    await FullSyncAsync(token);
                }

                return (personId, credentialId, factorId);
            });

            _log.LogInformation(
                $"VISO: ProvisionAsync completed ERPID={erpId}, PersonId={result.personId}, CredentialId={result.credentialId}, FactorId={result.factorId}");

            return result;
        }

        // --------------------------------------------------------------------
        // Create person (default group)
        // --------------------------------------------------------------------
        public async Task<int> CreatePersonAsync(string erpId, string firstName, string lastName)
        {
            _log.LogInformation(
                $"VISO: CreatePersonAsync requested for ERPID={erpId}, FirstName={firstName}, LastName={lastName}");

            var personId = await WithSessionRetry(async token =>
            {
                using var cfg = _clients.CreateConfig();

                var id = await cfg.InsertPersonAsync(new PersonData
                {
                    Name = erpId,
                    FirstName = firstName,
                    LastName = lastName,
                    GroupID = DefaultGroupId,
                    UserExternalIdentifier = erpId,
                    UserInformationReference = erpId,
                    DataProcessingPermission = true
                }, token);

                if (id <= 0) throw await MakeServiceError(cfg, "InsertPerson");
                return id;
            });

            _log.LogInformation(
                $"VISO: CreatePersonAsync succeeded for ERPID={erpId} => PersonId={personId}");

            return personId;
        }

        // ----- helpers -----
        private static string UniqueName(string?[] existing, string baseName)
        {
            var set = existing.Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = baseName;
            int i = 2;
            while (set.Contains(name)) name = $"{baseName}_{i++}";
            return name;
        }

        private static string NormalizeDec(string? s)
        {
            var d = new string((s ?? "").Where(char.IsDigit).ToArray());
            var nz = d.TrimStart('0');
            return nz.Length == 0 ? "0" : nz;
        }

        private async Task FullSyncAsync(Guid token)
        {
            _log.LogInformation("VISO: Running full configuration synchronization.");
            using var comm = _clients.CreateComm();
            var taskId = await comm.RunConfigurationSynchronizationAsync(token);
            if (taskId <= 0)
            {
                _log.LogError($"VISO: Full sync failed with code {taskId}.");
                throw new InvalidOperationException($"Full sync failed (code {taskId}).");
            }
            _log.LogInformation($"VISO: Full sync completed, TaskId={taskId}.");
        }

        private static async Task<Exception> MakeServiceError(ConfigurationQueryServiceClient cfg, string op)
        {
            try
            {
                var msg = await cfg.GetLastErrorMessageAsync();
                return new InvalidOperationException($"{op} failed: {msg}");
            }
            catch
            {
                return new InvalidOperationException($"{op} failed (see server logs).");
            }
        }

        private async Task<T> WithSessionRetry<T>(Func<Guid, Task<T>> action)
        {
            var token = await _session.GetTokenAsync();
            try
            {
                return await action(token);
            }
            catch (Exception ex) when (LooksLikeSessionExpired(ex))
            {
                _log.LogWarning(ex, "VISO: session expired or invalid. Refreshing token and retrying operation.");
                var token2 = await _session.RefreshTokenAsync();
                return await action(token2);
            }
        }

        private static bool LooksLikeSessionExpired(Exception ex)
        {
            var s = ex.ToString();
            // match only typical VISO/session faults
            return s.Contains("session expired", StringComparison.OrdinalIgnoreCase)
                || s.Contains("invalid session", StringComparison.OrdinalIgnoreCase)
                || s.Contains("token expired", StringComparison.OrdinalIgnoreCase)
                || s.Contains("token invalid", StringComparison.OrdinalIgnoreCase)
                || ex is System.ServiceModel.FaultException fe
                   && fe.Code?.Name?.Contains("Session", StringComparison.OrdinalIgnoreCase) == true;
        }

        // --------------------------------------------------------------------
        // GROUPS (read + update)
        // --------------------------------------------------------------------
        public async Task<IReadOnlyList<GroupData>> GetAllGroupsAsync()
        {
            _log.LogInformation("VISO: GetAllGroupsAsync start.");

            var result = await WithSessionRetry<IReadOnlyList<GroupData>>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                var groups = await cfg.GetGroupsAsync(token) ?? Array.Empty<GroupData>();

                IReadOnlyList<GroupData> ordered = groups
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();

                return ordered;
            });

            var count = result?.Count ?? 0;
            _log.LogInformation($"VISO: GetAllGroupsAsync completed, Count={count}");

            return result;
        }

        /// <summary>Lookup a VISO person by ERPID (Name) and return (personId, groupId).</summary>
        public async Task<(int personId, int groupId)?> TryGetPersonAndGroupByErpAsync(string erpId)
        {
            if (string.IsNullOrWhiteSpace(erpId)) return null;

            _log.LogInformation(
                $"VISO: TryGetPersonAndGroupByErpAsync start ERPID={erpId}");

            var result = await WithSessionRetry<(int, int)?>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                var all = await cfg.GetPersonsAsync(token);
                var person = (all ?? Array.Empty<PersonData>())
                            .FirstOrDefault(p => string.Equals(p.Name, erpId, StringComparison.Ordinal));

                if (person is null) return null;

                // Ensure GroupID populated (some contracts return lite rows)
                if (person.GroupID <= 0)
                    person = await cfg.GetPersonByIdAsync(person.ID, token) ?? person;

                var gid = person.GroupID is null ? 0 : Convert.ToInt32(person.GroupID);
                return (person.ID, gid);
            });

            if (result is null)
            {
                _log.LogInformation(
                    $"VISO: TryGetPersonAndGroupByErpAsync no person found for ERPID={erpId}");
            }
            else
            {
                _log.LogInformation(
                    $"VISO: TryGetPersonAndGroupByErpAsync found ERPID={erpId} => PersonId={result.Value.Item1}, GroupId={result.Value.Item2}");
            }

            return result;
        }

        /// <summary>Update a person’s group by personId and push changes to controllers.</summary>
        public async Task UpdatePersonGroupAsync(int personId, int newGroupId, bool autoFullSyncOnFailure = true)
        {
            _log.LogInformation(
                $"VISO: UpdatePersonGroupAsync start PersonId={personId}, NewGroupId={newGroupId}");

            await WithSessionRetry<object?>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                using var sync = _clients.CreateSync();
                using var comm = _clients.CreateComm();

                var p = await cfg.GetPersonByIdAsync(personId, token)
                        ?? throw new InvalidOperationException($"Person {personId} not found.");

                if (p.GroupID == newGroupId)
                {
                    _log.LogInformation(
                        $"VISO: UpdatePersonGroupAsync no-op; person already in GroupId={newGroupId}");
                    return null;
                }

                p.GroupID = newGroupId;

                var rc = await cfg.UpdatePersonAsync(p, token);
                if (rc != 0) throw await MakeServiceError(cfg, "UpdatePerson");

                var creds = await cfg.GetPersonCredentialsAsync(personId, token) ?? Array.Empty<CredentialData>();
                var credIds = creds.Select(c => c.ID).Where(i => i > 0).ToArray();

                if (credIds.Length > 0)
                {
                    try
                    {
                        var s = await sync.PartialCredentialsSynchronizationAsync(credIds, token);
                        if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                        {
                            _log.LogWarning(
                                $"VISO: UpdatePersonGroupAsync partial sync rc={s}; running full sync.");
                            await comm.RunConfigurationSynchronizationAsync(token);
                        }
                    }
                    catch when (autoFullSyncOnFailure)
                    {
                        _log.LogWarning(
                            "VISO: UpdatePersonGroupAsync partial sync threw; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }
                else if (autoFullSyncOnFailure)
                {
                    _log.LogInformation(
                        "VISO: UpdatePersonGroupAsync no credentials to sync; running full sync.");
                    await comm.RunConfigurationSynchronizationAsync(token);
                }

                return null;
            });

            _log.LogInformation(
                $"VISO: UpdatePersonGroupAsync completed PersonId={personId}, NewGroupId={newGroupId}");
        }

        // --------------------------------------------------------------------
        // Replacement flows helpers
        // --------------------------------------------------------------------
        public async Task<IReadOnlyList<CredentialData>> GetPersonCredentialsAsync(int personId)
        {
            _log.LogInformation(
                $"VISO: GetPersonCredentialsAsync start PersonId={personId}");

            var result = await WithSessionRetry<IReadOnlyList<CredentialData>>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                var creds = await cfg.GetPersonCredentialsAsync(personId, token) ?? Array.Empty<CredentialData>();

                IReadOnlyList<CredentialData> ordered = creds
                    .OrderBy(c => c.ID)
                    .ToArray();

                return ordered;
            });

            var count = result?.Count ?? 0;
            _log.LogInformation(
                $"VISO: GetPersonCredentialsAsync completed PersonId={personId}, Count={count}");

            return result;
        }

        public async Task<IReadOnlyList<FactorData>> GetFactorsByCredentialIdAsync(int credentialId)
        {
            _log.LogInformation(
                $"VISO: GetFactorsByCredentialIdAsync start CredentialId={credentialId}");

            var result = await WithSessionRetry<IReadOnlyList<FactorData>>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                var f = await cfg.GetAuthenticationFactorsByCredentialIdAsync(credentialId, token) ?? Array.Empty<FactorData>();

                IReadOnlyList<FactorData> ordered = f
                    .OrderBy(x => x.ID)
                    .ToArray();

                return ordered;
            });

            var count = result?.Count ?? 0;
            _log.LogInformation(
                $"VISO: GetFactorsByCredentialIdAsync completed CredentialId={credentialId}, Count={count}");

            return result;
        }

        /// <summary>Unassign a credential from a person (does NOT delete the credential object) and sync controllers.</summary>
        public async Task UnassignCredentialFromPersonAsync(int personId, int credentialId, bool autoFullSyncOnFailure = true)
        {
            _log.LogInformation(
                $"VISO: UnassignCredentialFromPersonAsync start PersonId={personId}, CredentialId={credentialId}");

            await WithSessionRetry<object?>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                using var sync = _clients.CreateSync();
                using var comm = _clients.CreateComm();

                int rc;
                var miAsync = cfg.GetType().GetMethod("UnassignCredentialAsync", new[] { typeof(int), typeof(int), typeof(Guid) });
                if (miAsync != null)
                {
                    dynamic t = miAsync.Invoke(cfg, new object[] { personId, credentialId, token });
                    rc = await t;
                }
                else
                {
                    var mi = cfg.GetType().GetMethod("UnassignCredential", new[] { typeof(int), typeof(int), typeof(Guid) })
                             ?? throw new MissingMethodException("ConfigurationQueryServiceClient.UnassignCredential[Async] not found.");
                    rc = await Task.Run(() => (int)mi.Invoke(cfg, new object[] { personId, credentialId, token })!);
                }

                if (rc != 0) throw await MakeServiceError(cfg, "UnassignCredential");

                try
                {
                    var s = await sync.PartialCredentialsSynchronizationAsync(new[] { credentialId }, token);
                    if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                    {
                        _log.LogWarning(
                            $"VISO: UnassignCredentialFromPersonAsync partial sync rc={s}; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }
                catch when (autoFullSyncOnFailure)
                {
                    _log.LogWarning(
                        "VISO: UnassignCredentialFromPersonAsync partial sync threw; running full sync.");
                    await comm.RunConfigurationSynchronizationAsync(token);
                }

                return null;
            });

            _log.LogInformation(
                $"VISO: UnassignCredentialFromPersonAsync completed PersonId={personId}, CredentialId={credentialId}");
        }

        /// <summary>Assign an existing credential to a person and sync.</summary>
        public async Task AssignExistingCredentialToPersonAsync(int personId, int credentialId, bool autoFullSyncOnFailure = true)
        {
            _log.LogInformation(
                $"VISO: AssignExistingCredentialToPersonAsync start PersonId={personId}, CredentialId={credentialId}");

            await WithSessionRetry<object?>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                using var sync = _clients.CreateSync();
                using var comm = _clients.CreateComm();

                var rc = await cfg.AssignCredentialsAsync(personId, new[] { credentialId }, token);
                if (rc < 0) throw await MakeServiceError(cfg, "AssignCredentials");

                try
                {
                    var s = await sync.PartialCredentialsSynchronizationAsync(new[] { credentialId }, token);
                    if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                    {
                        _log.LogWarning(
                            $"VISO: AssignExistingCredentialToPersonAsync partial sync rc={s}; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }
                catch when (autoFullSyncOnFailure)
                {
                    _log.LogWarning(
                        "VISO: AssignExistingCredentialToPersonAsync partial sync threw; running full sync.");
                    await comm.RunConfigurationSynchronizationAsync(token);
                }

                return null;
            });

            _log.LogInformation(
                $"VISO: AssignExistingCredentialToPersonAsync completed PersonId={personId}, CredentialId={credentialId}");
        }

        /// <summary>
        /// Find an UNASSIGNED credential intended as a replacement (Name contains "Zastępcza")
        /// that has a factor with the given DEC card number. Returns credentialId or null.
        /// Uses a fast path (all creds + all factors), then falls back to per-credential lookup
        /// if the proxy doesn't expose CredentialID on FactorData.
        /// </summary>
        public async Task<int?> FindUnassignedReplacementCredentialIdByCardDecAsync(string dec)
        {
            var want = NormalizeDec(dec);
            if (string.IsNullOrWhiteSpace(want)) return null;

            _log.LogInformation(
                $"VISO: FindUnassignedReplacementCredentialIdByCardDecAsync start DEC={want}");

            var result = await WithSessionRetry<int?>(async token =>
            {
                using var cfg = _clients.CreateConfig();

                // Unassigned credentials (prefer async if available)
                CredentialData[] allUnassigned;
                {
                    var miAsync = cfg.GetType().GetMethod("GetCredentialsNotAssignedToUsersAsync", new[] { typeof(Guid) });
                    if (miAsync != null)
                    {
                        dynamic t = miAsync.Invoke(cfg, new object[] { token });
                        allUnassigned = await t ?? Array.Empty<CredentialData>();
                    }
                    else
                    {
                        var mi = cfg.GetType().GetMethod("GetCredentialsNotAssignedToUsers", new[] { typeof(Guid) })
                                 ?? throw new MissingMethodException("GetCredentialsNotAssignedToUsers method not found.");
                        allUnassigned = await Task.Run(() =>
                            ((IEnumerable<CredentialData>)mi.Invoke(cfg, new object[] { token })!).ToArray());
                    }
                }

                var candidates = (allUnassigned ?? Array.Empty<CredentialData>())
                                 .Where(c => !string.IsNullOrWhiteSpace(c.Name) &&
                                             c.Name.IndexOf("Zastępcza", StringComparison.OrdinalIgnoreCase) >= 0)
                                 .ToArray();

                _log.LogInformation(
                    $"VISO: FindUnassignedReplacementCredentialIdByCardDecAsync candidates={candidates.Length}");

                if (candidates.Length == 0) return null;

                // Fast path: try joining via all factors (if FactorData exposes CredentialID)
                try
                {
                    FactorData[] allFactors;
                    var miAllFAsync = cfg.GetType().GetMethod("GetAllAuthenticationFactorsAsync", new[] { typeof(Guid) });
                    if (miAllFAsync != null)
                    {
                        dynamic t = miAllFAsync.Invoke(cfg, new object[] { token });
                        allFactors = await t ?? Array.Empty<FactorData>();
                    }
                    else
                    {
                        var miAllF = cfg.GetType().GetMethod("GetAllAuthenticationFactors", new[] { typeof(Guid) });
                        allFactors = miAllF != null
                            ? await Task.Run(() =>
                                ((IEnumerable<FactorData>)miAllF.Invoke(cfg, new object[] { token })!).ToArray())
                            : Array.Empty<FactorData>();
                    }

                    if (allFactors.Length > 0)
                    {
                        var credIdProp = allFactors[0].GetType().GetProperty("CredentialID")
                                         ?? allFactors[0].GetType().GetProperty("CredentialId");
                        if (credIdProp != null)
                        {
                            var candidateIds = candidates.Select(c => c.ID).ToHashSet();
                            foreach (var f in allFactors)
                            {
                                if (NormalizeDec(f?.Value) != want) continue;
                                var cidObj = credIdProp.GetValue(f);
                                if (cidObj is null) continue;
                                var cid = Convert.ToInt32(cidObj);
                                if (cid > 0 && candidateIds.Contains(cid)) return cid;
                            }
                        }
                    }
                }
                catch
                {
                    // fall back
                }

                // Fallback: per-credential factor lookup (normalize both sides)
                foreach (var c in candidates.OrderBy(c => c.ID))
                {
                    var factors = await cfg.GetAuthenticationFactorsByCredentialIdAsync(c.ID, token)
                                  ?? Array.Empty<FactorData>();
                    if (factors.Any(f => NormalizeDec(f?.Value) == want))
                        return c.ID;
                }

                return null;
            });

            if (result is null)
            {
                _log.LogInformation(
                    $"VISO: FindUnassignedReplacementCredentialIdByCardDecAsync no matching replacement credential for DEC={want}");
            }
            else
            {
                _log.LogInformation(
                    $"VISO: FindUnassignedReplacementCredentialIdByCardDecAsync found CredentialId={result.Value} for DEC={want}");
            }

            return result;
        }

        // --------------------------------------------------------------------
        // Issue main card
        // --------------------------------------------------------------------
        public async Task IssueMainCardAsync(string erpId, string cardDec, bool autoFullSyncOnFailure = true)
        {
            if (string.IsNullOrWhiteSpace(erpId)) throw new ArgumentException("Brak ERPID.");
            var dec = NormalizeDec(cardDec);
            if (string.IsNullOrWhiteSpace(dec)) throw new InvalidOperationException("Numer karty (DEC) jest pusty.");

            _log.LogInformation(
                $"VISO: IssueMainCardAsync start ERPID={erpId}, DEC={dec}");

            await WithSessionRetry<object?>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                using var sync = _clients.CreateSync();
                using var comm = _clients.CreateComm();

                // Find person
                var pg = await TryGetPersonAndGroupByErpAsync(erpId)
                         ?? throw new InvalidOperationException($"Osoba o ERPID {erpId} nie istnieje w VISO.");
                var personId = pg.personId;

                _log.LogInformation(
                    $"VISO: IssueMainCardAsync person resolved ERPID={erpId} => PersonId={personId}");

                // Load current credentials
                var creds = await cfg.GetPersonCredentialsAsync(personId, token) ?? Array.Empty<CredentialData>();

                var toDelete = creds
                    .Where(c => string.IsNullOrWhiteSpace(c.Name) ||
                                !c.Name.Contains("Zastępcza", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.ID)
                    .Where(id => id > 0)
                    .ToArray();

                _log.LogInformation(
                    $"VISO: IssueMainCardAsync will delete {toDelete.Length} non-replacement credentials for PersonId={personId}");

                foreach (var cid in toDelete)
                {
                    var factors = await cfg.GetAuthenticationFactorsByCredentialIdAsync(cid, token) ?? Array.Empty<FactorData>();
                    foreach (var f in factors)
                        _ = await cfg.RemoveAuthenticationFactorAsync(f.ID, token);

                    var rc = await cfg.DeleteCredentialAsync(cid, autoUnlinkRelatedObjects: true, returnFactorsToCardbox: false, token);
                    if (rc != 0) throw await MakeServiceError(cfg, $"DeleteCredential (ID={cid})");
                }

                if (toDelete.Length > 0)
                {
                    try
                    {
                        var s = await sync.PartialCredentialsSynchronizationAsync(toDelete, token);
                        if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                        {
                            _log.LogWarning(
                                $"VISO: IssueMainCardAsync partial sync rc={s}; running full sync.");
                            await comm.RunConfigurationSynchronizationAsync(token);
                        }
                    }
                    catch when (autoFullSyncOnFailure)
                    {
                        _log.LogWarning(
                            "VISO: IssueMainCardAsync partial sync threw; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }

                var baseName = string.IsNullOrWhiteSpace(erpId) ? "Karta" : erpId;
                var existingNames = (await cfg.GetPersonCredentialsAsync(personId, token) ?? Array.Empty<CredentialData>())
                                    .Select(c => c.Name).ToArray();
                var credName = UniqueName(existingNames!, baseName);

                var newCredId = await cfg.InsertCredentialAsync(new CredentialData { Name = credName }, token);
                if (newCredId <= 0) throw await MakeServiceError(cfg, "InsertCredential");

                _log.LogInformation(
                    $"VISO: IssueMainCardAsync new credential created PersonId={personId}, CredentialId={newCredId}, Name={credName}");

                var arc = await cfg.AssignCredentialsAsync(personId, new[] { newCredId }, token);
                if (arc < 0) throw await MakeServiceError(cfg, "AssignCredentials");

                var types = await cfg.GetFactorTypesAsync(token) ?? Array.Empty<FactorTypeData>();
                var cardType =
                       types.FirstOrDefault(ft => string.Equals(ft.Name, "Card40B", StringComparison.OrdinalIgnoreCase))
                    ?? types.FirstOrDefault(ft => ft.ID == 6)
                    ?? types.FirstOrDefault()
                    ?? throw new InvalidOperationException("Brak typu czynnika dla karty.");

                var factorId = await cfg.InsertAuthenticationFactorAsync(newCredId, new FactorData
                {
                    Name = credName,
                    Value = dec,
                    FactorTypeID = cardType.ID,
                    Disable = 0,
                    Tag = 0,
                    Index = 0
                }, token);
                if (factorId <= 0) throw await MakeServiceError(cfg, "InsertAuthenticationFactor");

                _log.LogInformation(
                    $"VISO: IssueMainCardAsync factor created CredentialId={newCredId}, FactorId={factorId}");

                try
                {
                    var s = await sync.PartialCredentialsSynchronizationAsync(new[] { newCredId }, token);
                    if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                    {
                        _log.LogWarning(
                            $"VISO: IssueMainCardAsync partial sync rc={s}; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }
                catch when (autoFullSyncOnFailure)
                {
                    _log.LogWarning(
                        "VISO: IssueMainCardAsync partial sync threw; running full sync.");
                    await comm.RunConfigurationSynchronizationAsync(token);
                }

                _log.LogInformation(
                    $"VISO: IssueMainCardAsync completed ERPID={erpId}, PersonId={personId}, CredentialId={newCredId}, FactorId={factorId}");

                return null;
            });
        }

        // --------------------------------------------------------------------
        // Delete person
        // --------------------------------------------------------------------
        public async Task DeletePersonByErpAsync(string erpId, bool autoFullSyncOnFailure = true)
        {
            if (string.IsNullOrWhiteSpace(erpId))
                throw new ArgumentException("Brak ERPID.");

            _log.LogInformation(
                $"VISO: DeletePersonByErpAsync start ERPID={erpId}");

            await WithSessionRetry<object?>(async token =>
            {
                using var cfg = _clients.CreateConfig();
                using var sync = _clients.CreateSync();
                using var comm = _clients.CreateComm();

                // Find person
                var pg = await TryGetPersonAndGroupByErpAsync(erpId);
                if (pg is null)
                {
                    _log.LogInformation(
                        $"VISO: DeletePersonByErpAsync no-op; person not found ERPID={erpId}");
                    return null; // nothing to do in VISO
                }
                var personId = pg.Value.personId;

                _log.LogInformation(
                    $"VISO: DeletePersonByErpAsync found person ERPID={erpId} => PersonId={personId}");

                // Load current credentials
                var creds = await cfg.GetPersonCredentialsAsync(personId, token) ?? Array.Empty<CredentialData>();

                var replacementCreds = creds
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name) &&
                                c.Name.IndexOf("Zastępcza", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();

                var nonReplacementIds = creds
                    .Where(c => string.IsNullOrWhiteSpace(c.Name) ||
                                c.Name.IndexOf("Zastępcza", StringComparison.OrdinalIgnoreCase) < 0)
                    .Select(c => c.ID)
                    .Where(id => id > 0)
                    .ToArray();

                _log.LogInformation(
                    $"VISO: DeletePersonByErpAsync PersonId={personId}, ReplacementCreds={replacementCreds.Length}, NonReplacementCreds={nonReplacementIds.Length}");

                // --- Safety net: unassign all replacement credentials so they are NOT deleted ---
                foreach (var rc in replacementCreds)
                {
                    int urc;
                    var miAsync = cfg.GetType().GetMethod("UnassignCredentialAsync", new[] { typeof(int), typeof(int), typeof(Guid) });
                    if (miAsync != null)
                    {
                        dynamic t = miAsync.Invoke(cfg, new object[] { personId, rc.ID, token });
                        urc = await t;
                    }
                    else
                    {
                        var mi = cfg.GetType().GetMethod("UnassignCredential", new[] { typeof(int), typeof(int), typeof(Guid) })
                                 ?? throw new MissingMethodException("ConfigurationQueryServiceClient.UnassignCredential[Async] not found.");
                        urc = await Task.Run(() => (int)mi.Invoke(cfg, new object[] { personId, rc.ID, token })!);
                    }
                    if (urc != 0) throw await MakeServiceError(cfg, "UnassignCredential");

                    // Sync the unassignment for this credential
                    try
                    {
                        var s = await sync.PartialCredentialsSynchronizationAsync(new[] { rc.ID }, token);
                        if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                        {
                            _log.LogWarning(
                                $"VISO: DeletePersonByErpAsync partial sync (unassign) rc={s}; running full sync.");
                            await comm.RunConfigurationSynchronizationAsync(token);
                        }
                    }
                    catch when (autoFullSyncOnFailure)
                    {
                        _log.LogWarning(
                            "VISO: DeletePersonByErpAsync partial sync (unassign) threw; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }

                // --- Delete the person; remaining (non-replacement) credentials will be deleted ---
                int del = await cfg.DeletePersonAsync(personId, autoUnlinkRelatedObjects: true, deleteAssignedCredentials: true, token);
                if (del != 0) throw await MakeServiceError(cfg, "DeletePerson");

                _log.LogInformation(
                    $"VISO: DeletePersonByErpAsync person deleted ERPID={erpId}, PersonId={personId}");

                // Targeted sync for the deleted (non-replacement) credential IDs
                if (nonReplacementIds.Length > 0)
                {
                    try
                    {
                        var s = await sync.PartialCredentialsSynchronizationAsync(nonReplacementIds, token);
                        if (s != 0 && autoFullSyncOnFailure && (s is 258 or 262 or 255 or 257 or 259))
                        {
                            _log.LogWarning(
                                $"VISO: DeletePersonByErpAsync partial sync (delete) rc={s}; running full sync.");
                            await comm.RunConfigurationSynchronizationAsync(token);
                        }
                    }
                    catch when (autoFullSyncOnFailure)
                    {
                        _log.LogWarning(
                            "VISO: DeletePersonByErpAsync partial sync (delete) threw; running full sync.");
                        await comm.RunConfigurationSynchronizationAsync(token);
                    }
                }

                return null;
            });

            _log.LogInformation(
                $"VISO: DeletePersonByErpAsync completed ERPID={erpId}");
        }

        // --------------------------------------------------------------------
        // Update names
        // --------------------------------------------------------------------
        public async Task UpdatePersonNamesAsync(string erpId, string firstName, string lastName)
        {
            if (string.IsNullOrWhiteSpace(erpId)) return;

            _log.LogInformation(
                $"VISO: UpdatePersonNamesAsync start ERPID={erpId}, FirstName={firstName}, LastName={lastName}");

            await WithSessionRetry<object?>(async token =>
            {
                using var cfg = _clients.CreateConfig();

                // Find person by ERPID (Name)
                var pg = await TryGetPersonAndGroupByErpAsync(erpId);
                if (pg is null)
                {
                    _log.LogInformation(
                        $"VISO: UpdatePersonNamesAsync no-op; person not found ERPID={erpId}");
                    return null;
                }

                var p = await cfg.GetPersonByIdAsync(pg.Value.personId, token)
                        ?? throw new InvalidOperationException($"Osoba {erpId} nie istnieje w VISO.");

                bool changed = false;
                if (!string.Equals(p.FirstName ?? "", firstName ?? "", StringComparison.Ordinal))
                {
                    p.FirstName = firstName ?? "";
                    changed = true;
                }
                if (!string.Equals(p.LastName ?? "", lastName ?? "", StringComparison.Ordinal))
                {
                    p.LastName = lastName ?? "";
                    changed = true;
                }

                if (!changed)
                {
                    _log.LogInformation(
                        $"VISO: UpdatePersonNamesAsync no-op; names unchanged ERPID={erpId}, PersonId={p.ID}");
                    return null;
                }

                var rc = await cfg.UpdatePersonAsync(p, token);
                if (rc != 0) throw await MakeServiceError(cfg, "UpdatePerson");

                _log.LogInformation(
                    $"VISO: UpdatePersonNamesAsync updated names ERPID={erpId}, PersonId={p.ID}");

                // No sync needed for name-only changes.
                return null;
            });

            _log.LogInformation(
                $"VISO: UpdatePersonNamesAsync completed ERPID={erpId}");
        }
    }
}