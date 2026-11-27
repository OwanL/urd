import { render } from "preact";
import { UserQuestions } from "../../chat/ask-user/UserQuestions.tsx";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { UserQuestion } from "../../sessions/inputs/UserQuestion.ts";

const root = document.createElement("main");
document.body.append(root);
document.head.insertAdjacentHTML(
  "beforeend",
  `<style>
    * { box-sizing: border-box; }
    body { margin: 0; font: 14px sans-serif; }
    main { height: 600px; width: 600px; display: flex; flex-direction: column; }
    #history { flex: 1 1 0; min-height: 0; overflow: auto; overflow-anchor: none; }
    #history p { height: 24px; margin: 0; }
    #composer { flex: none; height: 56px; }
  </style>`,
);

let questions: UserQuestion[] = [];
let notice = 0;
const client = {
  getUserQuestions: async () => questions,
  answerUserQuestion: async (_session: string, id: string, answer: string) => {
    questions = questions.filter((question) => question.id !== id);
    return { answer };
  },
} as unknown as SessionClient;

function show(next: UserQuestion[]) {
  questions = next;
  notice++;
  render(
    <>
      <div id="history">
        {Array.from({ length: 100 }, (_, i) => (
          <p id={`row-${i}`}>Row {i}</p>
        ))}
      </div>
      <UserQuestions client={client} sessionId="fixture" connected notice={String(notice)} />
      <div id="composer">
        <input aria-label="Composer" />
      </div>
    </>,
    root,
  );
}
show([]);
Object.assign(window, { showQuestions: show });
