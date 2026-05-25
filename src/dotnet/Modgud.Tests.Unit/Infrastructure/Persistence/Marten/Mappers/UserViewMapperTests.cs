using BuildingBlocks.Helper;
using Modgud.Infrastructure.Persistence.Marten.Mappers;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Tests.Unit.Infrastructure.Persistence.Marten.Mappers;

/// <summary>
/// Pins the projection-to-DTO mapping in <see cref="UserViewMapper.ToDto"/>.
/// Two contracts: nullable strings collapse to <c>string.Empty</c> for the
/// required first/last-name fields, and every Guid (user id + IdP-config ids)
/// is rendered as a <see cref="ShortGuid"/> string the frontend expects.
/// </summary>
public class UserViewMapperTests
{
    public class ToDto
    {
        [Fact]
        public void Maps_all_fields_one_to_one()
        {
            var id = Guid.NewGuid();
            var idp1 = Guid.NewGuid();
            var idp2 = Guid.NewGuid();
            var view = new UserView
            {
                Id = id,
                UserName = "alice",
                Firstname = "Alice",
                Lastname = "Brown",
                Acronym = "AB",
                Email = "alice@example.com",
                IsActive = true,
                HasPassword = true,
                ExternalLoginProviderIds = new List<Guid> { idp1, idp2 },
            };

            var dto = view.ToDto();

            Assert.Equal(new ShortGuid(id).ToString(), dto.Id);
            Assert.Equal("alice", dto.UserName);
            Assert.Equal("Alice", dto.Firstname);
            Assert.Equal("Brown", dto.Lastname);
            Assert.Equal("AB", dto.Acronym);
            Assert.Equal("alice@example.com", dto.Email);
            Assert.True(dto.IsActive);
            Assert.True(dto.HasPassword);
            Assert.Equal(2, dto.ExternalLoginProviderIds.Count);
            Assert.Contains(new ShortGuid(idp1).ToString(), dto.ExternalLoginProviderIds);
            Assert.Contains(new ShortGuid(idp2).ToString(), dto.ExternalLoginProviderIds);
        }

        [Fact]
        public void Null_firstname_and_lastname_become_empty_strings()
        {
            // The frontend treats Firstname/Lastname as required strings; the
            // mapper bridges UserView's nullable shape by collapsing to empty.
            var view = new UserView
            {
                Id = Guid.NewGuid(),
                Firstname = null,
                Lastname = null,
                UserName = "alice",
            };

            var dto = view.ToDto();

            Assert.Equal(string.Empty, dto.Firstname);
            Assert.Equal(string.Empty, dto.Lastname);
        }

        [Fact]
        public void Optional_fields_pass_null_through()
        {
            var view = new UserView
            {
                Id = Guid.NewGuid(),
                Acronym = null,
                Email = null,
                UserName = null,
            };

            var dto = view.ToDto();

            Assert.Null(dto.Acronym);
            Assert.Null(dto.Email);
            Assert.Null(dto.UserName);
        }

        [Fact]
        public void Empty_idp_list_maps_to_empty_string_list()
        {
            var view = new UserView { Id = Guid.NewGuid(), ExternalLoginProviderIds = new() };

            var dto = view.ToDto();

            Assert.NotNull(dto.ExternalLoginProviderIds);
            Assert.Empty(dto.ExternalLoginProviderIds);
        }

        [Fact]
        public void Inactive_user_is_propagated()
        {
            var view = new UserView { Id = Guid.NewGuid(), IsActive = false };
            var dto = view.ToDto();
            Assert.False(dto.IsActive);
        }

        [Fact]
        public void HasPassword_false_is_propagated()
        {
            var view = new UserView { Id = Guid.NewGuid(), HasPassword = false };
            var dto = view.ToDto();
            Assert.False(dto.HasPassword);
        }
    }
}
