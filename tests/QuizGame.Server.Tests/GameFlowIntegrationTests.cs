using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using QuizGame.Contracts;

namespace QuizGame.Server.Tests;

public class GameFlowIntegrationTests(QuizServerFactory factory) : IClassFixture<QuizServerFactory>
{
    private async Task<string> LoginAsAdminAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(QuizServerFactory.AdminUsername, QuizServerFactory.AdminPassword));
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        return auth.Token;
    }

    [Fact]
    public async Task Rest_Auth_And_QuestionBank_Crud_HappyPath()
    {
        using var anon = factory.CreateClient();

        var health = await anon.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var unauthRoom = await anon.PostAsJsonAsync("/api/rooms", new RoomSettings());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRoom.StatusCode);

        var badLogin = await anon.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(QuizServerFactory.AdminUsername, "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        var token = await LoginAsAdminAsync(anon);
        using var gm = factory.CreateAuthedClient(token);

        var catRes = await gm.PostAsJsonAsync("/api/categories", new CategoryUpsertRequest { Name = $"Geo-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, catRes.StatusCode);
        var category = (await catRes.Content.ReadFromJsonAsync<CategoryDto>())!;

        var mcRes = await gm.PostAsJsonAsync("/api/questions", new QuestionUpsertRequest
        {
            CategoryId = category.Id,
            Type = QuestionType.MultipleChoice,
            Text = "Capital of Hungary?",
            Difficulty = 1,
            TimeLimitSeconds = 15,
            Options =
            [
                new OptionUpsert('A', "Vienna", false),
                new OptionUpsert('B', "Budapest", true),
            ],
        });
        Assert.Equal(HttpStatusCode.Created, mcRes.StatusCode);
        var mc = (await mcRes.Content.ReadFromJsonAsync<QuestionDto>())!;

        var estRes = await gm.PostAsJsonAsync("/api/questions", new QuestionUpsertRequest
        {
            CategoryId = category.Id,
            Type = QuestionType.Estimation,
            Text = "Height of Kekes in meters?",
            Difficulty = 2,
            TimeLimitSeconds = 15,
            CorrectNumber = 1014m,
            Unit = "m",
        });
        Assert.Equal(HttpStatusCode.Created, estRes.StatusCode);
        var est = (await estRes.Content.ReadFromJsonAsync<QuestionDto>())!;

        var list = await gm.GetFromJsonAsync<PagedResult<QuestionDto>>($"/api/questions?categoryId={category.Id}");
        Assert.NotNull(list);
        Assert.Equal(2, list.TotalCount);

        var del = await gm.DeleteAsync($"/api/questions/{est.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterDelete = await gm.GetFromJsonAsync<PagedResult<QuestionDto>>($"/api/questions?categoryId={category.Id}");
        Assert.NotNull(afterDelete);
        Assert.Single(afterDelete.Items);
        Assert.Equal(mc.Id, afterDelete.Items[0].Id);
    }

    [Fact]
    public async Task Full_SignalR_Game_Flow_From_Lobby_To_Results()
    {
        using var anon = factory.CreateClient();
        var gmToken = await LoginAsAdminAsync(anon);
        using var gmApi = factory.CreateAuthedClient(gmToken);

        var cat = await (await gmApi.PostAsJsonAsync("/api/categories",
            new CategoryUpsertRequest { Name = $"Flow-{Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<CategoryDto>();
        var mc = await (await gmApi.PostAsJsonAsync("/api/questions", new QuestionUpsertRequest
        {
            CategoryId = cat!.Id,
            Type = QuestionType.MultipleChoice,
            Text = "2 + 2 = ?",
            Difficulty = 1,
            TimeLimitSeconds = 15,
            Options =
            [
                new OptionUpsert('A', "3", false),
                new OptionUpsert('B', "4", true),
            ],
        })).Content.ReadFromJsonAsync<QuestionDto>();
        var est = await (await gmApi.PostAsJsonAsync("/api/questions", new QuestionUpsertRequest
        {
            CategoryId = cat.Id,
            Type = QuestionType.Estimation,
            Text = "How many years in a century?",
            Difficulty = 1,
            TimeLimitSeconds = 15,
            CorrectNumber = 100m,
            Unit = "years",
        })).Content.ReadFromJsonAsync<QuestionDto>();

        var createdRoom = await (await gmApi.PostAsJsonAsync("/api/rooms", new RoomSettings
        {
            MapPreset = MapPreset.Small,
            ConquestRounds = 1,
            DuelRounds = 1,
            BaseHealth = 1,
            PickTimeoutSeconds = 10,
            DisallowAdjacentBases = true,
        })).Content.ReadFromJsonAsync<CreateRoomResponse>();
        var code = createdRoom!.RoomCode;

        await using var host = await factory.ConnectHubAsync(gmToken);
        await host.Connection.InvokeAsync(HubNames.Methods.HostAttach, code);
        var hostLobbyState = await host.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);
        Assert.Equal(GamePhase.Lobby, hostLobbyState.Phase);

        var joinAnna = await (await anon.PostAsJsonAsync($"/api/rooms/{code}/join", new JoinRequest("Anna"))).Content.ReadFromJsonAsync<JoinResponse>();
        var joinBela = await (await anon.PostAsJsonAsync($"/api/rooms/{code}/join", new JoinRequest("Bela"))).Content.ReadFromJsonAsync<JoinResponse>();

        await using var anna = await factory.ConnectHubAsync(joinAnna!.Token);
        await using var bela = await factory.ConnectHubAsync(joinBela!.Token);
        await anna.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);
        await bela.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);

        // Player tokens cannot call host hub methods.
        await Assert.ThrowsAsync<HubException>(() => anna.Connection.InvokeAsync(HubNames.Methods.HostAttach, code));
        await Assert.ThrowsAsync<HubException>(() => anna.Connection.InvokeAsync(HubNames.Methods.StartGame));

        // ---- BaseSelection ----
        host.ClearEvents();
        anna.ClearEvents();
        bela.ClearEvents();
        await host.Connection.InvokeAsync(HubNames.Methods.StartGame);

        Assert.Equal(GamePhase.BaseSelection, await host.WaitForAsync<GamePhase>(HubNames.Events.PhaseChanged));
        var startState = await host.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);
        var firstPickerId = startState.TurnOrder[0];
        var (firstHub, secondHub) = firstPickerId == joinAnna.Player.Id ? (anna, bela) : (bela, anna);

        var firstPick = await firstHub.WaitForAsync<PickRequest>(HubNames.Events.PickRequested, p => p.Purpose == PickPurpose.Base);
        var waitingNotice = await secondHub.WaitForAsync<PickWaitingDto>(HubNames.Events.PickWaiting, w => w.Purpose == PickPurpose.Base);
        Assert.Equal(firstPickerId, waitingNotice.Picker.Id);
        Assert.True(waitingNotice.PickDeadlineUtc > startState.ServerUtcNow);

        // First picker picks tile 0; second picker is blocked from picking adjacent tile 1.
        await firstHub.Connection.InvokeAsync(HubNames.Methods.PickTerritory, 0);
        var secondPick = await secondHub.WaitForAsync<PickRequest>(HubNames.Events.PickRequested, p => p.Purpose == PickPurpose.Base);
        Assert.DoesNotContain(1, secondPick.ValidTerritoryIndices);
        var adjEx = await Assert.ThrowsAsync<HubException>(() => secondHub.Connection.InvokeAsync(HubNames.Methods.PickTerritory, 1));
        Assert.Contains(ErrorCodes.InvalidPick, adjEx.Message);

        await secondHub.Connection.InvokeAsync(HubNames.Methods.PickTerritory, secondPick.ValidTerritoryIndices[0]);
        Assert.Equal(GamePhase.Conquest, await host.WaitForAsync<GamePhase>(HubNames.Events.PhaseChanged, p => p == GamePhase.Conquest));

        // ---- Conquest ----
        host.ClearEvents();
        anna.ClearEvents();
        bela.ClearEvents();
        await host.Connection.InvokeAsync(HubNames.Methods.SendQuestion, new SendQuestionRequest { QuestionId = mc!.Id });

        var claim1 = await firstHub.WaitForAsync<PickRequest>(HubNames.Events.PickRequested, p => p.Purpose == PickPurpose.ConquestClaim);
        await firstHub.Connection.InvokeAsync(HubNames.Methods.PickTerritory, claim1.ValidTerritoryIndices[0]);

        var claim2 = await secondHub.WaitForAsync<PickRequest>(HubNames.Events.PickRequested, p => p.Purpose == PickPurpose.ConquestClaim);
        await secondHub.Connection.InvokeAsync(HubNames.Methods.PickTerritory, claim2.ValidTerritoryIndices[0]);

        var annaCq = await anna.WaitForAsync<QuestionForPlayer>(HubNames.Events.QuestionStarted);
        var belaCq = await bela.WaitForAsync<QuestionForPlayer>(HubNames.Events.QuestionStarted);
        Assert.Equal(2, annaCq.Options.Count);

        var accA = await anna.Connection.InvokeAsync<AnswerAccepted>(
            HubNames.Methods.SubmitAnswer, new SubmitAnswerRequest { RoundId = annaCq.RoundId, Option = 'B' });
        var accB = await bela.Connection.InvokeAsync<AnswerAccepted>(
            HubNames.Methods.SubmitAnswer, new SubmitAnswerRequest { RoundId = belaCq.RoundId, Option = 'B' });
        Assert.Equal(annaCq.RoundId, accA.RoundId);
        Assert.Equal(belaCq.RoundId, accB.RoundId);

        var cqClosed = await host.WaitForAsync<RoundClosedDto>(HubNames.Events.RoundClosed, r => r.RoundId == annaCq.RoundId);
        Assert.Equal(2, cqClosed.WinnerPlayerIds.Count);
        Assert.Equal(GamePhase.Expansion, await host.WaitForAsync<GamePhase>(HubNames.Events.PhaseChanged, p => p == GamePhase.Expansion));

        // ---- Expansion: fill remaining free tiles ----
        for (var guard = 0; guard < 25; guard++)
        {
            var snap = await gmApi.GetFromJsonAsync<RoomStateDto>($"/api/rooms/{code}");
            if (snap!.Phase == GamePhase.Duel) break;

            var hostCursor = host.EventCount;
            var annaCursor = anna.EventCount;
            var belaCursor = bela.EventCount;

            await host.Connection.InvokeAsync(HubNames.Methods.SendQuestion, new SendQuestionRequest { QuestionId = est!.Id });
            var annaEq = await anna.WaitForAsync<QuestionForPlayer>(HubNames.Events.QuestionStarted, afterIndex: annaCursor);
            var belaEq = await bela.WaitForAsync<QuestionForPlayer>(HubNames.Events.QuestionStarted, afterIndex: belaCursor);

            await anna.Connection.InvokeAsync(HubNames.Methods.SubmitAnswer, new SubmitAnswerRequest { RoundId = annaEq.RoundId, Number = 100m });
            await bela.Connection.InvokeAsync(HubNames.Methods.SubmitAnswer, new SubmitAnswerRequest { RoundId = belaEq.RoundId, Number = 50m });

            await host.WaitForAsync<RoundClosedDto>(HubNames.Events.RoundClosed, r => r.RoundId == annaEq.RoundId, afterIndex: hostCursor);
            var expPick = await anna.WaitForAsync<PickRequest>(HubNames.Events.PickRequested, p => p.Purpose == PickPurpose.Expansion, afterIndex: annaCursor);
            await anna.Connection.InvokeAsync(HubNames.Methods.PickTerritory, expPick.ValidTerritoryIndices[0]);
            await host.WaitForAsync<MapUpdate>(HubNames.Events.MapUpdated, afterIndex: hostCursor);
        }

        Assert.Equal(GamePhase.Duel, await host.WaitForAsync<GamePhase>(HubNames.Events.PhaseChanged, p => p == GamePhase.Duel));

        // ---- Duel -> Results ----
        var duelSnap = await gmApi.GetFromJsonAsync<RoomStateDto>($"/api/rooms/{code}");
        var attackerId = duelSnap!.TurnOrder[0];
        var (attackerHub, defenderHub) = attackerId == joinAnna.Player.Id ? (anna, bela) : (bela, anna);

        var targetPick = await attackerHub.WaitForAsync<PickRequest>(HubNames.Events.PickRequested, p => p.Purpose == PickPurpose.DuelTarget);
        var hostDuelCursor = host.EventCount;
        var attackerCursor = attackerHub.EventCount;
        var defenderCursor = defenderHub.EventCount;

        await attackerHub.Connection.InvokeAsync(HubNames.Methods.NominateTarget, targetPick.ValidTerritoryIndices[0]);
        var nomination = await host.WaitForAsync<DuelNominationDto>(HubNames.Events.DuelNominated, afterIndex: hostDuelCursor);
        Assert.Equal(attackerId, nomination.AttackerId);

        await host.Connection.InvokeAsync(HubNames.Methods.ConfirmDuel, new ConfirmDuelRequest { QuestionId = mc.Id });
        var attackerDq = await attackerHub.WaitForAsync<QuestionForPlayer>(HubNames.Events.QuestionStarted, afterIndex: attackerCursor);
        var defenderDq = await defenderHub.WaitForAsync<QuestionForPlayer>(HubNames.Events.QuestionStarted, afterIndex: defenderCursor);

        await attackerHub.Connection.InvokeAsync(HubNames.Methods.SubmitAnswer, new SubmitAnswerRequest { RoundId = attackerDq.RoundId, Option = 'B' });
        await defenderHub.Connection.InvokeAsync(HubNames.Methods.SubmitAnswer, new SubmitAnswerRequest { RoundId = defenderDq.RoundId, Option = 'A' });

        var duelClosed = await host.WaitForAsync<RoundClosedDto>(HubNames.Events.RoundClosed, r => r.RoundId == attackerDq.RoundId, afterIndex: hostDuelCursor);
        Assert.Equal(RoundKind.Duel, duelClosed.Kind);

        Assert.Equal(GamePhase.Results, await host.WaitForAsync<GamePhase>(HubNames.Events.PhaseChanged, p => p == GamePhase.Results));
        var finished = await host.WaitForAsync<GameFinishedDto>(HubNames.Events.GameFinished);
        Assert.Equal(2, finished.Standings.Count);
        Assert.Equal(25, finished.FinalMap.Territories.Count);

        // ---- Room close ----
        var annaCloseCursor = anna.EventCount;
        var deleteRes = await gmApi.DeleteAsync($"/api/rooms/{code}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
        await anna.WaitForSignalAsync(HubNames.Events.RoomClosed, afterIndex: annaCloseCursor);
    }

    [Fact]
    public async Task Player_Rejoin_And_Kick_Enforce_Session_State()
    {
        using var anon = factory.CreateClient();
        var gmToken = await LoginAsAdminAsync(anon);
        using var gmApi = factory.CreateAuthedClient(gmToken);

        var createdRoom = await (await gmApi.PostAsJsonAsync("/api/rooms", new RoomSettings()))
            .Content.ReadFromJsonAsync<CreateRoomResponse>();
        var code = createdRoom!.RoomCode;

        await using var host = await factory.ConnectHubAsync(gmToken);
        await host.Connection.InvokeAsync(HubNames.Methods.HostAttach, code);
        await host.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);

        var joinAnna = await (await anon.PostAsJsonAsync($"/api/rooms/{code}/join", new JoinRequest("Anna"))).Content.ReadFromJsonAsync<JoinResponse>();
        var joinBela = await (await anon.PostAsJsonAsync($"/api/rooms/{code}/join", new JoinRequest("Bela"))).Content.ReadFromJsonAsync<JoinResponse>();

        await using var anna = await factory.ConnectHubAsync(joinAnna!.Token);
        await using var bela = await factory.ConnectHubAsync(joinBela!.Token);
        await anna.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);
        await bela.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);

        await host.Connection.InvokeAsync(HubNames.Methods.StartGame);
        var phase = await host.WaitForAsync<GamePhase>(HubNames.Events.PhaseChanged);
        Assert.Equal(GamePhase.BaseSelection, phase);

        // REST rejoin returns same player identity.
        using var annaApi = factory.CreateAuthedClient(joinAnna.Token);
        var restRejoin = await annaApi.PostAsync($"/api/rooms/{code}/rejoin", null);
        Assert.Equal(HttpStatusCode.OK, restRejoin.StatusCode);
        var rejoined = await restRejoin.Content.ReadFromJsonAsync<JoinResponse>();
        Assert.Equal(joinAnna.Player.Id, rejoined!.Player.Id);

        // SignalR Rejoin returns fresh RoomState where ValidTerritoryIndices are populated only for the picker.
        await using var anna2 = await factory.ConnectHubAsync(rejoined.Token);
        var initial = await anna2.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState);
        var cursor = anna2.EventCount;
        await anna2.Connection.InvokeAsync(HubNames.Methods.Rejoin);
        var snapshot = await anna2.WaitForAsync<RoomStateDto>(HubNames.Events.RoomState, afterIndex: cursor);

        Assert.NotEqual(default, snapshot.ServerUtcNow);
        Assert.Equal(GamePhase.BaseSelection, snapshot.Phase);
        Assert.NotNull(snapshot.CurrentPick);
        if (snapshot.CurrentPick.PickerPlayerId == joinAnna.Player.Id)
            Assert.NotEmpty(snapshot.CurrentPick.ValidTerritoryIndices);
        else
            Assert.Empty(snapshot.CurrentPick.ValidTerritoryIndices);
        Assert.Equal(initial.RoomCode, snapshot.RoomCode);

        // Host kicks Bela -> Bela receives Kicked, host gets PlayerLeft, and REST rejoin is rejected.
        var belaCursor = bela.EventCount;
        var hostCursor = host.EventCount;
        await host.Connection.InvokeAsync(HubNames.Methods.KickPlayer, joinBela.Player.Id);

        await bela.WaitForSignalAsync(HubNames.Events.Kicked, afterIndex: belaCursor);
        var left = await host.WaitForAsync<PlayerSummary>(HubNames.Events.PlayerLeft, p => p.Id == joinBela.Player.Id, afterIndex: hostCursor);
        Assert.Equal("Bela", left.Name);

        using var belaApi = factory.CreateAuthedClient(joinBela.Token);
        var kickedRejoin = await belaApi.PostAsync($"/api/rooms/{code}/rejoin", null);
        Assert.Equal(HttpStatusCode.NotFound, kickedRejoin.StatusCode);
    }
}
