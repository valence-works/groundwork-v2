import unittest
from io import BytesIO
from unittest.mock import patch
from urllib.error import HTTPError, URLError

from eng.project_board_sync import (
    DONE,
    IN_PROGRESS,
    TODO,
    build_sync_plan,
    closing_issue_numbers,
    derive_status,
    GitHubError,
    GitHubClient,
    is_roadmap_issue,
    roadmap_issues,
    reconcile,
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
            {
                "state": "open",
                "base": {
                    "ref": "Main",
                    "repo": {"full_name": REPOSITORY},
                },
                "body": "Fixes #22",
                "commits": [{"commit": {"message": "Fixes #23"}}],
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


class SnapshotTests(unittest.TestCase):
    def test_snapshot_pages_fields_and_items_and_reads_status_by_name(self):
        status_field = {
            "__typename": "ProjectV2SingleSelectField",
            "id": "status-field",
            "name": "Status",
            "options": [
                {"id": "todo", "name": TODO},
                {"id": "progress", "name": IN_PROGRESS},
                {"id": "done", "name": DONE},
            ],
        }
        issue_item = {
            "id": "item-17",
            "content": {
                "__typename": "Issue",
                "number": 17,
                "repository": {"nameWithOwner": REPOSITORY},
            },
            "statusValue": {
                "__typename": "ProjectV2ItemFieldSingleSelectValue",
                "name": TODO,
                "optionId": "todo",
            },
        }

        def payload(project):
            return {"data": {"organization": {"projectV2": project}}}

        responses = iter(
            [
                payload(
                    {
                        "id": "project-6",
                        "fields": {
                            "nodes": [],
                            "pageInfo": {"hasNextPage": True, "endCursor": "fields-1"},
                        },
                    }
                ),
                payload(
                    {
                        "id": "project-6",
                        "fields": {
                            "nodes": [status_field],
                            "pageInfo": {"hasNextPage": False, "endCursor": None},
                        },
                    }
                ),
                payload(
                    {
                        "id": "project-6",
                        "items": {
                            "nodes": [],
                            "pageInfo": {"hasNextPage": True, "endCursor": "items-1"},
                        },
                    }
                ),
                payload(
                    {
                        "id": "project-6",
                        "items": {
                            "nodes": [issue_item],
                            "pageInfo": {"hasNextPage": False, "endCursor": None},
                        },
                    }
                ),
            ]
        )

        class FakeClient(GitHubClient):
            def __init__(self):
                super().__init__("token", api_url="https://example.test")
                self.requests = []

            def _request(self, method, url, body=None):
                self.requests.append((method, url, body))
                return next(responses)

        client = FakeClient()
        snapshot = client.get_project_snapshot("valence-works", 6)

        self.assertEqual("project-6", snapshot["id"])
        self.assertEqual("status-field", snapshot["status_field_id"])
        self.assertEqual("todo", snapshot["status_options"][TODO])
        self.assertEqual(["item-17"], [item["id"] for item in snapshot["items"]])
        self.assertEqual(
            [None, "fields-1", None, "items-1"],
            [request[2]["variables"]["after"] for request in client.requests],
        )
        self.assertIn("fieldValueByName(name: \"Status\")", client.requests[2][2]["query"])
        self.assertNotIn("fieldValues(first: 100)", client.requests[2][2]["query"])


class ClientResilienceTests(unittest.TestCase):
    @staticmethod
    def http_error(status, headers=None, body=b"failure"):
        return HTTPError(
            "https://example.test/resource",
            status,
            "failure",
            headers or {},
            BytesIO(body),
        )

    class Response:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc_value, traceback):
            return False

        @staticmethod
        def read():
            return b"[]"

    def test_retryable_get_succeeds_after_bounded_transient_failure(self):
        delays = []
        with patch(
            "eng.project_board_sync.urlopen",
            side_effect=[self.http_error(503), self.Response()],
        ):
            client = GitHubClient("token", sleep_fn=delays.append)
            self.assertEqual([], client._request("GET", "https://example.test/resource"))
        self.assertEqual([1.0], delays)

    def test_retryable_url_error_is_bounded(self):
        delays = []
        with patch(
            "eng.project_board_sync.urlopen",
            side_effect=[URLError("temporary network failure"), self.Response()],
        ):
            client = GitHubClient("token", sleep_fn=delays.append)
            self.assertEqual([], client._request("GET", "https://example.test/resource"))
        self.assertEqual([1.0], delays)

    def test_forbidden_request_retries_only_with_rate_limit_signal(self):
        delays = []
        with patch(
            "eng.project_board_sync.urlopen",
            side_effect=[
                self.http_error(403, {"Retry-After": "0"}),
                self.Response(),
            ],
        ):
            client = GitHubClient("token", sleep_fn=delays.append)
            self.assertEqual([], client._request("GET", "https://example.test/resource"))
        self.assertEqual([0.0], delays)

        delays = []
        with patch(
            "eng.project_board_sync.urlopen",
            side_effect=[self.http_error(403), self.Response()],
        ):
            client = GitHubClient("token", sleep_fn=delays.append)
            with self.assertRaises(GitHubError) as context:
                client._request("GET", "https://example.test/resource")
        self.assertEqual(403, context.exception.status)
        self.assertEqual([], delays)

    def test_retry_exhaustion_raises_and_reconcile_does_not_mutate(self):
        delays = []
        with patch(
            "eng.project_board_sync.urlopen",
            side_effect=[self.http_error(503), self.http_error(503), self.http_error(503)],
        ):
            client = GitHubClient("token", sleep_fn=delays.append)
            with self.assertRaises(GitHubError) as context:
                client._request("GET", "https://example.test/resource")
        self.assertEqual(503, context.exception.status)
        self.assertEqual([1.0, 2.0], delays)

        class FailingSnapshotClient:
            def __init__(self):
                self.mutations = []

            def list_roadmap_issues(self, repository):
                return [issue(24)]

            def get_default_branch(self, repository):
                return "main"

            def list_open_pull_requests(self, repository):
                raise GitHubError("commit lookup exhausted", status=503)

            def get_project_snapshot(self, organization, project_number):
                raise AssertionError("project must not be read after an incomplete PR snapshot")

            def add_project_item(self, project_id, content_id):
                self.mutations.append(("add", content_id))

            def update_project_status(self, project_id, item_id, field_id, option_id):
                self.mutations.append(("status", item_id))

        failing = FailingSnapshotClient()
        with self.assertRaises(GitHubError):
            reconcile(failing, REPOSITORY, "valence-works", 6)
        self.assertEqual([], failing.mutations)

    def test_disappeared_pull_request_is_dropped_from_snapshot(self):
        pull_request = {
            "number": 25,
            "state": "open",
            "base": {"repo": {"full_name": REPOSITORY}},
            "body": "Fixes #25",
        }

        for status in (404, 410):
            with self.subTest(status=status):
                class DisappearedClient(GitHubClient):
                    def __init__(self, disappearance_status):
                        super().__init__("token", sleep_fn=lambda delay: None)
                        self.disappearance_status = disappearance_status

                    def get_pages(self, path, **params):
                        if path.endswith("/pulls"):
                            return [pull_request]
                        raise GitHubError(
                            "pull request disappeared", status=self.disappearance_status
                        )

                client = DisappearedClient(status)
                self.assertEqual([], client.list_open_pull_requests(REPOSITORY))


if __name__ == "__main__":
    unittest.main()
