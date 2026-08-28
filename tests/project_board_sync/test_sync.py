import unittest

from eng.project_board_sync import (
    DONE,
    IN_PROGRESS,
    TODO,
    build_sync_plan,
    closing_issue_numbers,
    derive_status,
    is_roadmap_issue,
    roadmap_issues,
)


REPOSITORY = "valence-works/groundwork-v2"


def issue(number, *, state="open", labels=("roadmap-2.0",), assignees=(), repository=REPOSITORY):
    return {
        "number": number,
        "node_id": f"I_{number}",
        "state": state,
        "labels": [{"name": label} for label in labels],
        "assignees": [{"login": assignee} for assignee in assignees],
        "repository": {"full_name": repository},
    }


def project_issue_item(number, status, *, item_id=None):
    return {
        "id": item_id or f"PVTI_{number}",
        "content": {
            "__typename": "Issue",
            "number": number,
            "repository": {"nameWithOwner": REPOSITORY},
        },
        "fieldValues": {
            "nodes": [{"name": status, "field": {"name": "Status"}}],
        },
    }


class MembershipTests(unittest.TestCase):
    def test_membership_requires_the_roadmap_label_and_repository(self):
        self.assertTrue(is_roadmap_issue(issue(2)))
        self.assertFalse(is_roadmap_issue(issue(3, labels=("bug",))))
        self.assertFalse(is_roadmap_issue(issue(4, repository="other/project")))

    def test_membership_filters_pull_requests_and_sorts_historical_issues(self):
        pull_request = issue(9)
        pull_request["pull_request"] = {"url": "https://api.github.com/pulls/9"}
        selected = roadmap_issues([issue(8), pull_request, issue(1)])
        self.assertEqual([1, 8], [item["number"] for item in selected])


class StatusTests(unittest.TestCase):
    def test_closed_always_wins(self):
        self.assertEqual(DONE, derive_status(issue(1, state="closed", assignees=("alice",)), {1}))

    def test_open_assignment_is_in_progress(self):
        self.assertEqual(IN_PROGRESS, derive_status(issue(2, assignees=("alice",)), set()))

    def test_open_closing_pull_request_is_in_progress(self):
        self.assertEqual(IN_PROGRESS, derive_status(issue(3), {3}))

    def test_unassigned_open_issue_is_todo(self):
        self.assertEqual(TODO, derive_status(issue(4), set()))


class ClosingPullRequestTests(unittest.TestCase):
    def test_only_open_same_repository_closing_references_count(self):
        pull_requests = [
            {
                "state": "open",
                "base": {"repo": {"full_name": REPOSITORY}},
                "body": "Closes #10 and resolves valence-works/groundwork-v2#11.",
            },
            {
                "state": "open",
                "base": {"repo": {"full_name": REPOSITORY}},
                "body": "Refs #12 and fixes another-org/other-repo#13.",
            },
            {
                "state": "closed",
                "base": {"repo": {"full_name": REPOSITORY}},
                "body": "Fixes #14",
            },
        ]
        self.assertEqual({10, 11}, set(closing_issue_numbers(pull_requests)))

    def test_closing_url_is_supported(self):
        pull_requests = [
            {
                "state": "open",
                "base": {"repo": {"full_name": REPOSITORY}},
                "body": "Fixes: https://github.com/valence-works/groundwork-v2/issues/15",
            }
        ]
        self.assertEqual({15}, set(closing_issue_numbers(pull_requests)))

    def test_title_only_closing_keyword_does_not_count(self):
        pull_requests = [
            {
                "state": "open",
                "base": {"repo": {"full_name": REPOSITORY}},
                "title": "Closes #16",
                "body": "",
            }
        ]
        self.assertEqual(set(), set(closing_issue_numbers(pull_requests)))
        self.assertEqual(
            (),
            build_sync_plan(
                [issue(16)],
                pull_requests,
                [project_issue_item(16, TODO)],
            ),
        )

    def test_commit_only_closing_keyword_counts(self):
        pull_requests = [
            {
                "state": "open",
                "base": {"repo": {"full_name": REPOSITORY}},
                "body": "",
                "commits": [{"commit": {"message": "Fixes #17\n\nImplement it."}}],
            }
        ]
        self.assertEqual({17}, set(closing_issue_numbers(pull_requests)))
        self.assertEqual(
            [("status", 17, IN_PROGRESS, "existing-17", None)],
            [
                (a.kind, a.issue_number, a.status, a.item_id, a.content_id)
                for a in build_sync_plan(
                    [issue(17)],
                    pull_requests,
                    [project_issue_item(17, TODO, item_id="existing-17")],
                )
            ],
        )

    def test_non_default_base_closers_do_not_count(self):
        pull_requests = [
            {
                "state": "open",
                "base": {
                    "ref": "release/2.0",
                    "repo": {"full_name": REPOSITORY},
                },
                "body": "Fixes #18",
                "commits": [{"commit": {"message": "Fixes #19"}}],
            },
            {
                "state": "open",
                "base": {
                    "ref": "main",
                    "repo": {"full_name": REPOSITORY},
                },
                "body": "Fixes #20",
                "commits": [{"commit": {"message": "Fixes #21"}}],
            },
        ]
        self.assertEqual(
            {20, 21},
            set(closing_issue_numbers(pull_requests, default_branch="main")),
        )


class PlanTests(unittest.TestCase):
    def test_plan_adds_missing_issue_and_updates_only_stale_in_scope_item(self):
        actions = build_sync_plan(
            [issue(1), issue(2, assignees=("alice",)), issue(3, state="closed")],
            [],
            [
                project_issue_item(2, TODO, item_id="existing-2"),
                {
                    "id": "unrelated",
                    "content": {"__typename": "Issue", "number": 99, "repository": {"nameWithOwner": "other/project"}},
                },
                {
                    "id": "a-pr",
                    "content": {"__typename": "PullRequest", "number": 1, "repository": {"nameWithOwner": REPOSITORY}},
                },
            ],
        )
        self.assertEqual(
            [
                ("add", 1, TODO, None, "I_1"),
                ("status", 2, IN_PROGRESS, "existing-2", None),
                ("add", 3, DONE, None, "I_3"),
            ],
            [(a.kind, a.issue_number, a.status, a.item_id, a.content_id) for a in actions],
        )


if __name__ == "__main__":
    unittest.main()
