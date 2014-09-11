package com.tinkerpop.gremlin.structure.util.detached;

import com.tinkerpop.gremlin.process.graph.GraphTraversal;
import com.tinkerpop.gremlin.structure.Direction;
import com.tinkerpop.gremlin.structure.Edge;
import com.tinkerpop.gremlin.structure.Graph;
import com.tinkerpop.gremlin.structure.MetaProperty;
import com.tinkerpop.gremlin.structure.Property;
import com.tinkerpop.gremlin.structure.Vertex;
import com.tinkerpop.gremlin.structure.io.util.IoMetaProperty;
import com.tinkerpop.gremlin.structure.util.StringFactory;
import org.javatuples.Pair;

import java.io.Serializable;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

/**
 * Represents a {@link Vertex} that is disconnected from a {@link Graph}.  "Disconnection" can mean detachment from
 * a {@link Graph} in the sense that a {@link Vertex} was constructed from a {@link Graph} instance and this reference
 * was removed or it can mean that the {@code DetachedVertex} could have been constructed independently of a
 * {@link Graph} instance in the first place.
 * <br/>
 * A {@code DetachedVertex} only has reference to the properties that are associated with it at the time of detachment
 * (or construction) and is not traversable or mutable.
 *
 * @author Stephen Mallette (http://stephen.genoprime.com)
 * @author Marko A. Rodriguez (http://markorodriguez.com)
 */
public class DetachedVertex extends DetachedElement<Vertex> implements Vertex {

    private DetachedVertex() { }

    public DetachedVertex(final Object id, final String label, final Map<String, Object> properties, final Map<String, Object> hiddenProperties) {
        super(id, label);
        if (null != properties) this.properties.putAll(convertToDetachedMetaProperties(properties));
        if (null != hiddenProperties) this.properties.putAll(convertToDetachedMetaProperties(hiddenProperties));
    }

    protected DetachedVertex(final Object id, final String label) {
        super(id, label);
    }

    private DetachedVertex(final Vertex vertex) {
        super(vertex);
    }

    @Override
    public <V> MetaProperty<V> property(final String key, final V value) {
        throw new UnsupportedOperationException("Detached elements are readonly: " + this);
    }

    @Override
    public <V> MetaProperty<V> property(final String key) {
        if (this.properties.containsKey(key)) {
            final List<MetaProperty> list = (List) this.properties.get(key);
            if (list.size() > 1)
                throw Vertex.Exceptions.multiplePropertiesExistForProvidedKey(key);
            else
                return list.get(0);
        } else
            return MetaProperty.<V>empty();
    }

    @Override
    public Edge addEdge(final String label, final Vertex inVertex, final Object... keyValues) {
        throw new UnsupportedOperationException("Detached vertices do not store edges: " + this);
    }

    @Override
    public String toString() {
        return StringFactory.vertexString(this);
    }

    @Override
    public GraphTraversal<Vertex, Vertex> start() {
        throw new UnsupportedOperationException("Detached vertices cannot be traversed: " + this);
    }

    @Override
    public Vertex attach(final Vertex hostVertex) {
        if (!hostVertex.id().toString().equals(this.id.toString())) // TODO: Why is this bad?
            throw new IllegalStateException("The host vertex must be the vertex trying to be attached: " +
                    hostVertex.id() + "!=" + this.id() + " or " +
                    hostVertex.id().getClass() + "!=" + this.id().getClass());
        return hostVertex;
    }

    @Override
    public Vertex attach(final Graph graph) {
        return graph.v(this.id);
    }

    public static DetachedVertex detach(final Vertex vertex) {
        if (null == vertex) throw Graph.Exceptions.argumentCanNotBeNull("vertex");
        return new DetachedVertex(vertex);
    }

    @Override
    public Vertex.Iterators iterators() {
        return this.iterators;
    }

    private Map<String, List<Property>> convertToDetachedMetaProperties(final Map<String, Object> hiddenProperties) {
        return hiddenProperties.entrySet().stream()
                .map(entry -> Pair.with(entry.getKey(), ((List<IoMetaProperty>) entry.getValue()).stream()
                                .map(iom -> (Property) new DetachedMetaProperty(iom.id, iom.label, entry.getKey(), iom.value, (DetachedVertex) this))
                                .collect(Collectors.toList()))
                ).collect(Collectors.toMap(p -> p.getValue0(), p -> p.getValue1()));
    }

    private final Vertex.Iterators iterators = new Iterators();

    protected class Iterators extends DetachedElement<Vertex>.Iterators implements Vertex.Iterators, Serializable {

        @Override
        public <V> Iterator<MetaProperty<V>> properties(final String... propertyKeys) {
            return (Iterator) super.properties(propertyKeys);
        }

        @Override
        public <V> Iterator<MetaProperty<V>> hiddens(final String... propertyKeys) {
            return (Iterator) super.hiddens(propertyKeys);
        }

        @Override
        public GraphTraversal<Vertex, Edge> edges(final Direction direction, final int branchFactor, final String... labels) {
            throw new IllegalStateException();
        }

        @Override
        public GraphTraversal<Vertex, Vertex> vertices(final Direction direction, final int branchFactor, final String... labels) {
            throw new IllegalStateException();
        }
    }
}
