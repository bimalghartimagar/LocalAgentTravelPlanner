# Spec: Multi-Agent Travel Planner with Automated Auditor

## 1. Objective
Build a production-grade travel planning system using the **Microsoft Agent Framework (MAF)** and **Nuxt 3**. The system must orchestrate multiple specialized agents to generate a 100% verified itinerary.

## 2. Technical Stack
- **Backend:** .NET 9 Web API + Microsoft Agent Framework.
- **Frontend:** Nuxt 3 (Vue) + AG-UI Protocol for real-time streaming.
- **Inference:** Local Ollama (Llama-3 for workers, Llama-3-70B for the Auditor).
- **Communication:** SignalR or AG-UI SSE for "thought streaming."

## 3. Agent Personas
| Agent | Role | Responsibility |
| :--- | :--- | :--- |
| **Researcher** | Data Gatherer | Finds locations (e.g., Butwal to Pokhara), hotels, and transit. |
| **Accountant** | Budgeter | Calculates all costs in NPR/USD and ensures math consistency. |
| **Planner** | Aggregator | Formats the data into a readable, logical timeline. |
| **Auditor** | Evaluator | **Critical Node:** Validates the output of all agents before final delivery. |

## 4. Evaluation Criteria (The Auditor's Rubric)
The Auditor Agent must grade the final output on a scale of 1-5 across these metrics:
1. **Financial Integrity:** Sum of all line items must equal the total. Total must be <= user budget.
2. **Temporal Logic:** No impossible travel times (e.g., cannot be in two cities at once).
3. **Safety & Compliance:** Refuse plans for restricted zones or dangerous activities.
4. **Groundedness:** All suggested hotels and sites must be verified through the Research Agent's context.

## 5. Success Scenarios (Test Cases)
- **Scenario A:** 3-day family trip from Butwal to Pokhara. Budget: 50,000 NPR.
- **Scenario B (Adversarial):** Luxury 10-day tour of Nepal for $100. (Expected: Auditor flags as "Impossible").

## 6. UI/UX Requirements
- **Live Thought Stream:** Nuxt frontend must show which agent is currently active.
- **Audit Badge:** Display the Auditor's score and "Reasoning" clearly next to the itinerary.