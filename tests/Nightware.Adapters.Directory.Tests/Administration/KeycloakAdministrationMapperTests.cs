using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Directory.Tests.Administration;

public sealed class KeycloakAdministrationMapperTests
{
    private static KeycloakUser User(Dictionary<string, IReadOnlyList<string>>? attributes = null) => new()
    {
        Id = "u1",
        Username = "carol",
        Email = "carol@acme.example",
        FirstName = "Carol",
        LastName = "  ",
        Enabled = true,
        Attributes = attributes,
    };

    [Fact]
    public void Maps_the_identity_fields()
    {
        IdentityAccount account = KeycloakAdministrationMapper.ToIdentity(User());

        account.Id.ShouldBe("u1");
        account.Username.ShouldBe("carol");
        account.Email.ShouldBe("carol@acme.example");
        account.Enabled.ShouldBeTrue();
        account.FirstName.ShouldBe("Carol");
        account.LastName.ShouldBeNull(); // blank reads as absent
        account.Aliases.ShouldBeEmpty();
        account.MailDeletedAt.ShouldBeNull();
        account.GroupwareDeletedAt.ShouldBeNull();
    }

    [Fact]
    public void A_user_without_username_or_email_maps_to_empty_strings()
    {
        IdentityAccount account = KeycloakAdministrationMapper.ToIdentity(new KeycloakUser { Id = "svc" });

        account.Username.ShouldBeEmpty();
        account.Email.ShouldBeEmpty();
        account.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Maps_aliases_from_the_multivalued_attribute_skipping_blanks()
    {
        IdentityAccount account = KeycloakAdministrationMapper.ToIdentity(User(new() { ["nightware_aliases"] = ["c@acme.example", "", "sales@acme.example"] }));

        account.Aliases.ShouldBe(["c@acme.example", "sales@acme.example"]);
    }

    [Fact]
    public void Maps_deletion_markers_and_ignores_malformed_ones()
    {
        IdentityAccount account = KeycloakAdministrationMapper.ToIdentity(User(new()
        {
            ["nightware_mail_deleted_at"] = ["2026-11-10T09:30:00.0000000+00:00"],
            ["nightware_groupware_deleted_at"] = ["not a date"],
        }));

        account.MailDeletedAt.ShouldBe(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero));
        account.GroupwareDeletedAt.ShouldBeNull();
    }

    [Fact]
    public void Markers_round_trip_through_the_iso_format_in_utc()
    {
        DateTimeOffset at = new(2026, 11, 17, 21, 5, 7, 123, TimeSpan.FromHours(11));

        string text = KeycloakAdministrationMapper.FormatMarker(at);

        text.ShouldBe("2026-11-17T10:05:07.1230000+00:00");
        KeycloakAdministrationMapper.Marker(text).ShouldBe(at);
        KeycloakAdministrationMapper.Marker(null).ShouldBeNull();
    }

    [Fact]
    public void Maps_an_organization_to_an_entity_with_valid_sorted_distinct_domains()
    {
        Entity entity = KeycloakAdministrationMapper.ToEntity(new KeycloakOrganization
        {
            Id = "o1",
            Name = "Acme",
            Alias = "acme",
            Domains =
            [
                new KeycloakOrganizationDomain { Name = "zeta.example" },
                new KeycloakOrganizationDomain { Name = "Acme.Example" },
                new KeycloakOrganizationDomain { Name = "acme.example" },
                new KeycloakOrganizationDomain { Name = "not a domain" },
            ],
        });

        entity.Id.ShouldBe("o1");
        entity.Alias.ShouldBe("acme");
        entity.Name.ShouldBe("Acme");
        entity.Domains.Select(static d => d.Name).ShouldBe(["acme.example", "zeta.example"]);
    }

    [Fact]
    public void An_organization_without_domains_has_none()
    {
        KeycloakAdministrationMapper.ToEntity(new KeycloakOrganization { Id = "o1", Name = "Empty" }).Domains.ShouldBeEmpty();
    }

    [Fact]
    public void Reads_each_domains_spf_and_dmarc_choice_from_organization_attributes()
    {
        Entity entity = KeycloakAdministrationMapper.ToEntity(new KeycloakOrganization
        {
            Id = "o1",
            Name = "Acme",
            Domains = [new KeycloakOrganizationDomain { Name = "acme.example" }, new KeycloakOrganizationDomain { Name = "zeta.example" }, new KeycloakOrganizationDomain { Name = "plain.example" }],
            Attributes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["nightware_spf:acme.example"] = ["v=spf1 mx -all"],
                ["nightware_dmarc:acme.example"] = ["v=DMARC1; p=none"],
                ["nightware_dmarc:zeta.example"] = ["not dmarc"],
                ["nightware_spf:gone.example"] = ["v=spf1 -all"],
                ["billing"] = ["internal"],
            },
        });

        MailDomain acme = entity.Domains[0];
        entity.MailPolicies.Keys.ShouldBe([acme]);
        entity.MailPolicyFor(acme).Spf!.Value.ShouldBe("v=spf1 mx -all");
        entity.MailPolicyFor(acme).Dmarc!.Policy.ShouldBe("none");
        entity.MailPolicyFor(entity.Domains[2]).IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void An_organization_without_choices_maps_to_an_entity_equal_to_a_plain_one()
    {
        Entity entity = KeycloakAdministrationMapper.ToEntity(new KeycloakOrganization { Id = "o1", Name = "Acme", Alias = "acme" });
        entity.ShouldBe(new Entity("o1", "acme", "Acme", entity.Domains));
    }

    [Theory]
    [InlineData(RequiredAction.UpdatePassword, "UPDATE_PASSWORD")]
    [InlineData(RequiredAction.ConfigureTotp, "CONFIGURE_TOTP")]
    public void Required_actions_use_keycloaks_aliases(RequiredAction action, string alias) =>
        KeycloakAdminNames.Alias(action).ShouldBe(alias);

    [Fact]
    public void Mail_policy_attributes_are_named_per_domain()
    {
        MailDomain.TryParse("acme.example", out MailDomain? acme).ShouldBeTrue();
        KeycloakAdminNames.SpfAttribute(acme).ShouldBe("nightware_spf:acme.example");
        KeycloakAdminNames.DmarcAttribute(acme).ShouldBe("nightware_dmarc:acme.example");
    }

    [Theory]
    [InlineData(DeletionTarget.Mail, "nightware_mail_deleted_at")]
    [InlineData(DeletionTarget.GroupwareData, "nightware_groupware_deleted_at")]
    public void Deletion_targets_name_their_marker_attribute(DeletionTarget target, string attribute) =>
        KeycloakAdminNames.MarkerAttribute(target).ShouldBe(attribute);

    [Fact]
    public void Unknown_enum_values_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => KeycloakAdminNames.Alias((RequiredAction)99));
        Should.Throw<ArgumentOutOfRangeException>(() => KeycloakAdminNames.MarkerAttribute((DeletionTarget)99));
    }
}
