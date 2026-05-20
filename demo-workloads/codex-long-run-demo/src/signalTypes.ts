export enum TaskState {
  Live = "Live",
  Working = "Working",
  RunningCommand = "RunningCommand",
  NeedsInput = "NeedsInput",
  Done = "Done",
}

export interface TaskSignal {
  state: TaskState;
  timestamp: Date;
  message?: string;
  progressPercent?: number;
}

export interface StateTransition {
  from: TaskState;
  to: TaskState;
  reason?: string;
}

export const StateTransitionRules: Record<TaskState, TaskState[]> = {
  [TaskState.Live]: [TaskState.Working],
  [TaskState.Working]: [TaskState.RunningCommand, TaskState.Done],
  [TaskState.RunningCommand]: [TaskState.NeedsInput, TaskState.Working],
  [TaskState.NeedsInput]: [TaskState.Working],
  [TaskState.Done]: [],
};
