export interface UserQuestion {
  id: string;
  askingSessionId: string;
  question: string;
  context: string | null;
  answers: string[];
  sourceTitle: string | null;
  background: boolean;
}
