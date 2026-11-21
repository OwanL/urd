export interface UserQuestion {
  id: string;
  question: string;
  context: string | null;
  answers: string[];
  sourceTitle: string | null;
  background: boolean;
}
