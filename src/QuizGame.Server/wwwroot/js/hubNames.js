// Mirror of QuizGame.Contracts.HubNames / ErrorCodes (docs/api-contract.md). Keep in sync.
export const HUB_PATH = '/hubs/game';

export const Methods = Object.freeze({
  HostAttach: 'HostAttach',
  KickPlayer: 'KickPlayer',
  UpdateSettings: 'UpdateSettings',
  StartGame: 'StartGame',
  AdvancePhase: 'AdvancePhase',
  SendQuestion: 'SendQuestion',
  CloseRound: 'CloseRound',
  ConfirmBattle: 'ConfirmBattle',
  SubmitAnswer: 'SubmitAnswer',
  PickTerritory: 'PickTerritory',
  NominateTarget: 'NominateTarget',
  Rejoin: 'Rejoin',
  LeaveRoom: 'LeaveRoom',
  Ping: 'Ping',
});

export const Events = Object.freeze({
  RoomState: 'RoomState',
  PlayerJoined: 'PlayerJoined',
  PlayerLeft: 'PlayerLeft',
  PlayerConnectionChanged: 'PlayerConnectionChanged',
  PhaseChanged: 'PhaseChanged',
  MapUpdated: 'MapUpdated',
  QuestionStarted: 'QuestionStarted',
  BattleAnnounced: 'BattleAnnounced',
  AnswerCountChanged: 'AnswerCountChanged',
  RoundClosed: 'RoundClosed',
  PickRequested: 'PickRequested',
  PickWaiting: 'PickWaiting',
  ScoreboardUpdated: 'ScoreboardUpdated',
  GameFinished: 'GameFinished',
  Kicked: 'Kicked',
  RoomClosed: 'RoomClosed',
  Error: 'Error',
});

export const ErrorCodes = Object.freeze({
  AnswerTooLate: 'AnswerTooLate',
  AlreadyAnswered: 'AlreadyAnswered',
  InvalidAnswer: 'InvalidAnswer',
  InvalidPhase: 'InvalidPhase',
  InvalidCredentials: 'InvalidCredentials',
  RoomNotFound: 'RoomNotFound',
  PlayerNotFound: 'PlayerNotFound',
  InvalidName: 'InvalidName',
  NameTaken: 'NameTaken',
  RoomFull: 'RoomFull',
  NotInLobby: 'NotInLobby',
  InvalidSettings: 'InvalidSettings',
  NotAuthorized: 'NotAuthorized',
  NotAttached: 'NotAttached',
  NotEnoughPlayers: 'NotEnoughPlayers',
  QuestionNotFound: 'QuestionNotFound',
  RoundInProgress: 'RoundInProgress',
  NoActiveRound: 'NoActiveRound',
  InvalidRound: 'InvalidRound',
  NotParticipant: 'NotParticipant',
  InvalidTransition: 'InvalidTransition',
  BaseSelectionIncomplete: 'BaseSelectionIncomplete',
  NotYourPick: 'NotYourPick',
  InvalidPick: 'InvalidPick',
});

// RoundKind / QuestionType are serialized as numbers (QuizGame.Contracts.RoundKind / QuestionType).
export const RoundKind = Object.freeze({ BaseOrder: 0, Grab: 1, Battle: 2 });
export const QuestionType = Object.freeze({ MultipleChoice: 0, Estimation: 1 });

// GamePhase is serialized as its numeric value (QuizGame.Contracts.GamePhase).
export const GamePhase = Object.freeze({
  Lobby: 0,
  BaseSelection: 1,
  Conquest: 2,
  Results: 3,
});
