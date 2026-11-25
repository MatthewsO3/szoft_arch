# M2 part D integration checks

Prerequisite: parts A/B/C must provide question CRUD/selection and round methods/events
using API contract §5. The current room-only server cannot run this checklist end to end.

1. Start the host, create a room, and join with two phones. Open **Projector**.
2. Send a multiple-choice question with 2 options, then one with 4. Phones show only
   options; the projector shows question text and options. Neither reveals the answer.
3. Tap an option twice quickly. Only one submission is sent. Input is locked while sending;
   “Answer received” appears only after a matching acknowledgment.
4. Close the round early. All clients stop counting down and show results. The projector
   marks the correct option and displays the ranking and winner(s).
5. Send an estimation question. Test zero, negative and fractional answers, decimal comma,
   empty input, and invalid text. Check unit display and distance/rank in results.
6. Let a round expire without answering. Inputs disable at zero; answers stay hidden until
   the server sends results. Check the no-answer result.
7. Set a phone clock several minutes wrong. Its countdown should approximately agree with
   the projector. Background the browser past the deadline and resume: no extra time.
8. Disconnect during submission. Input stays locked until server state confirms whether
   the answer was accepted. Check both accepted and unaccepted cases; refresh as well.
9. Reconnect while a question is active, and after missing its close event. Restore the
   correct question/accepted state or latest results without revealing answers early.
10. Close and reopen the projector during a question and during results. Check retained
    content, F11/Escape, resizing, long question/option text, and a long ranking.
11. Start another round while an earlier submission is pending. Its eventual response
    must not overwrite the new question. Repeat while leaving/kicking/closing the room.
12. Check both iOS Safari and Android Chrome with narrow screens and the numeric keyboard.

Automated client state/timing tests: `node --test tests/web/*.test.mjs`.
